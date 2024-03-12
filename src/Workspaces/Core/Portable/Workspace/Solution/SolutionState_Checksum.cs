﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Serialization;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis;

internal partial class SolutionState
{
    private static readonly ConditionalWeakTable<IReadOnlyList<ProjectId>, IReadOnlyList<ProjectId>> s_projectIdToSortedProjectsMap = new();

    /// <summary>
    /// Checksum representing the full checksum tree for this solution compilation state.  Includes the checksum for
    /// <see cref="SolutionState"/>.
    /// </summary>
    private readonly AsyncLazy<SolutionStateChecksums> _lazyChecksums;

    /// <summary>
    /// Mapping from project-id to the checksums needed to synchronize it (and the projects it depends on) over 
    /// to an OOP host.  Lock this specific field before reading/writing to it.
    /// </summary>
    private readonly Dictionary<ProjectId, AsyncLazy<(SolutionStateChecksums stateChecksums, ProjectCone projectCone)>> _lazyProjectChecksums = [];

    public static IReadOnlyList<ProjectId> GetOrCreateSortedProjectIds(IReadOnlyList<ProjectId> unorderedList)
        => s_projectIdToSortedProjectsMap.GetValue(unorderedList, projectIds => projectIds.OrderBy(id => id.Id).ToImmutableArray());

    public bool TryGetStateChecksums([NotNullWhen(true)] out SolutionStateChecksums? stateChecksums)
        => _lazyChecksums.TryGetValue(out stateChecksums);

    public bool TryGetStateChecksums(ProjectId projectId, [NotNullWhen(true)] out SolutionStateChecksums? stateChecksums)
    {
        AsyncLazy<(SolutionStateChecksums checksums, ProjectCone projectCone)>? lazyChecksums;
        lock (_lazyProjectChecksums)
        {
            if (!_lazyProjectChecksums.TryGetValue(projectId, out lazyChecksums) ||
                lazyChecksums == null)
            {
                stateChecksums = null;
                return false;
            }
        }

        if (lazyChecksums.TryGetValue(out var checksumsAndProjectCone))
        {
            stateChecksums = null;
            return false;
        }

        stateChecksums = checksumsAndProjectCone.checksums;
        return true;
    }

    public Task<SolutionStateChecksums> GetStateChecksumsAsync(CancellationToken cancellationToken)
        => _lazyChecksums.GetValueAsync(cancellationToken);

    public async Task<Checksum> GetChecksumAsync(CancellationToken cancellationToken)
    {
        var collection = await GetStateChecksumsAsync(cancellationToken).ConfigureAwait(false);
        return collection.Checksum;
    }

    /// <summary>Gets the checksum for only the requested project (and any project it depends on)</summary>
    public async Task<(SolutionStateChecksums stateChecksums, ProjectCone projectCone)> GetStateChecksumsAsync(
        ProjectId projectConeId,
        CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(projectConeId);

        AsyncLazy<(SolutionStateChecksums stateChecksums, ProjectCone projectCone)>? checksums;
        lock (_lazyProjectChecksums)
        {
            if (!_lazyProjectChecksums.TryGetValue(projectConeId, out checksums))
            {
                checksums = AsyncLazy.Create(static async (arg, cancellationToken) =>
                {
                    var (checksums, projectCone) = await arg.self.ComputeChecksumsAsync(arg.projectConeId, cancellationToken).ConfigureAwait(false);
                    Contract.ThrowIfNull(projectCone);
                    return (checksums, projectCone.Value);
                }, arg: (self: this, projectConeId));
                _lazyProjectChecksums.Add(projectConeId, checksums);
            }
        }

        var collection = await checksums.GetValueAsync(cancellationToken).ConfigureAwait(false);
        return collection;
    }

    /// <summary>Gets the checksum for only the requested project (and any project it depends on)</summary>
    public async Task<Checksum> GetChecksumAsync(ProjectId projectId, CancellationToken cancellationToken)
    {
        var (checksums, _) = await GetStateChecksumsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return checksums.Checksum;
    }

    /// <param name="projectConeId">Cone of projects to compute a checksum for.  Pass in <see langword="null"/> to get a
    /// checksum for the entire solution</param>
    private async Task<(SolutionStateChecksums stateChecksums, ProjectCone? projectCone)> ComputeChecksumsAsync(
        ProjectId? projectConeId,
        CancellationToken cancellationToken)
    {
        var projectConeSet = projectConeId is null ? null : new HashSet<ProjectId>();
        AddProjectCone(projectConeId);

        ProjectCone? projectCone = projectConeId is null
            ? null
            : new ProjectCone(projectConeId, SpecializedCollections.StronglyTypedReadOnlySet(projectConeSet!));

        try
        {
            using (Logger.LogBlock(FunctionId.SolutionState_ComputeChecksumsAsync, this.FilePath, cancellationToken))
            {
                // get states by id order to have deterministic checksum.  Limit expensive computation to the
                // requested set of projects if applicable.
                var orderedProjectIds = GetOrCreateSortedProjectIds(this.ProjectIds);

                using var _ = ArrayBuilder<Task<ProjectStateChecksums>>.GetInstance(out var projectChecksumTasks);

                foreach (var orderedProjectId in orderedProjectIds)
                {
                    var projectState = this.ProjectStates[orderedProjectId];
                    if (!RemoteSupportedLanguages.IsSupported(projectState.Language))
                        continue;

                    if (projectConeId != null && !projectConeSet!.Contains(orderedProjectId))
                        continue;

                    projectChecksumTasks.Add(projectState.GetStateChecksumsAsync(cancellationToken));
                }

                var allResults = await Task.WhenAll(projectChecksumTasks).ConfigureAwait(false);

                var projectChecksums = allResults.SelectAsArray(r => r.Checksum);
                var projectIds = allResults.SelectAsArray(r => r.ProjectId);

                var analyzerReferenceChecksums = ChecksumCache.GetOrCreateChecksumCollection(
                    this.AnalyzerReferences, this.Services.GetRequiredService<ISerializerService>(), cancellationToken);

                var stateChecksums = new SolutionStateChecksums(
                    projectConeId,
                    this.SolutionAttributes.Checksum,
                    new(new ChecksumCollection(projectChecksums), projectIds),
                    analyzerReferenceChecksums);

                return (stateChecksums, projectCone);
            }
        }
        catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e, cancellationToken))
        {
            throw ExceptionUtilities.Unreachable();
        }

        void AddProjectCone(ProjectId? projectConeId)
        {
            if (projectConeId is null || projectConeSet is null)
                return;

            if (!projectConeSet.Add(projectConeId))
                return;

            var projectState = this.GetProjectState(projectConeId);
            if (projectState == null)
                return;

            foreach (var refProject in projectState.ProjectReferences)
            {
                // Note: it's possible in the workspace to see project-ids that don't have a corresponding project
                // state.  While not desirable, we allow project's to have refs to projects that no longer exist
                // anymore.  This state is expected to be temporary until the project is explicitly told by the
                // host to remove the reference.  We do not expose this through the full Solution/Project which
                // filters out this case already (in Project.ProjectReferences). However, becausde we're at the
                // ProjectState level it cannot do that filtering unless examined through us (the SolutionState).
                if (this.ProjectStates.ContainsKey(refProject.ProjectId))
                    AddProjectCone(refProject.ProjectId);
            }
        }
    }
}

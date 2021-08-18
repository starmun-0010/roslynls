﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.DocumentHighlighting;
using Microsoft.CodeAnalysis.FindUsages;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableControl;

namespace Microsoft.VisualStudio.LanguageServices.FindUsages
{
    internal partial class StreamingFindUsagesPresenter
    {
        /// <summary>
        /// Context to be used for FindImplementations/GoToDef (as opposed to FindReferences).
        /// This context will not group entries by definition, and will instead just create
        /// entries for the definitions themselves.
        /// </summary>
        private class WithoutReferencesFindUsagesContext : AbstractTableDataSourceFindUsagesContext
        {
            public WithoutReferencesFindUsagesContext(
                StreamingFindUsagesPresenter presenter,
                IFindAllReferencesWindow findReferencesWindow,
                ImmutableArray<ITableColumnDefinition> customColumns,
                bool includeContainingTypeAndMemberColumns,
                bool includeKindColumn)
                : base(presenter, findReferencesWindow, customColumns, includeContainingTypeAndMemberColumns, includeKindColumn)
            {
            }

            // We should never be called in a context where we get references.
            protected override ValueTask OnReferenceFoundWorkerAsync(Solution solution, SourceReferenceItem reference, CancellationToken cancellationToken)
                => throw new InvalidOperationException();

            // Nothing to do on completion.
            protected override Task OnCompletedAsyncWorkerAsync(Solution solution, CancellationToken cancellationToken)
                => Task.CompletedTask;

            protected override async ValueTask OnDefinitionFoundWorkerAsync(Solution solution, DefinitionItem definition, CancellationToken cancellationToken)
            {
                var definitionBucket = GetOrCreateDefinitionBucket(definition, expandedByDefault: true);

                using var _ = ArrayBuilder<Entry>.GetInstance(out var entries);

                if (definition.SourceSpans.Length == 1)
                {
                    // If we only have a single location, then use the DisplayParts of the
                    // definition as what to show.  That way we show enough information for things
                    // methods.  i.e. we'll show "void TypeName.MethodName(args...)" allowing
                    // the user to see the type the method was created in.
                    var entry = await TryCreateEntryAsync(solution, definitionBucket, definition, cancellationToken).ConfigureAwait(false);
                    entries.AddIfNotNull(entry);
                }
                else if (definition.SourceSpans.Length == 0)
                {
                    // No source spans means metadata references.
                    // Display it for Go to Base and try to navigate to metadata.
                    entries.Add(new MetadataDefinitionItemEntry(this, definitionBucket));
                }
                else
                {
                    // If we have multiple spans (i.e. for partial types), then create a 
                    // DocumentSpanEntry for each.  That way we can easily see the source
                    // code where each location is to help the user decide which they want
                    // to navigate to.
                    foreach (var sourceSpan in definition.SourceSpans)
                    {
                        var entry = await TryCreateDocumentSpanEntryAsync(
                            solution,
                            definitionBucket,
                            sourceSpan,
                            HighlightSpanKind.Definition,
                            symbolUsageInfo: SymbolUsageInfo.None,
                            additionalProperties: definition.DisplayableProperties,
                            cancellationToken).ConfigureAwait(false);
                        entries.AddIfNotNull(entry);
                    }
                }

                if (entries.Count > 0)
                {
                    lock (Gate)
                    {
                        EntriesWhenGroupingByDefinition = EntriesWhenGroupingByDefinition.AddRange(entries);
                        EntriesWhenNotGroupingByDefinition = EntriesWhenNotGroupingByDefinition.AddRange(entries);
                    }

                    NotifyChange();
                }
            }

            private async Task<Entry?> TryCreateEntryAsync(
                Solution solution, RoslynDefinitionBucket definitionBucket, DefinitionItem definition, CancellationToken cancellationToken)
            {
                var documentSpan = await definition.SourceSpans[0].TryRehydrateAsync(solution, cancellationToken).ConfigureAwait(false);
                if (documentSpan == null)
                    return null;

                var (guid, projectName, _) = GetGuidAndProjectInfo(documentSpan.Value.Document);
                var sourceText = await documentSpan.Value.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);

                var lineText = AbstractDocumentSpanEntry.GetLineContainingPosition(sourceText, documentSpan.Value.SourceSpan.Start);
                var mappedDocumentSpan = await AbstractDocumentSpanEntry.TryMapAndGetFirstAsync(documentSpan.Value, sourceText, cancellationToken).ConfigureAwait(false);
                if (mappedDocumentSpan == null)
                {
                    // this will be removed from the result
                    return null;
                }

                return new DefinitionItemEntry(this, definitionBucket, projectName, guid, lineText, mappedDocumentSpan.Value);
            }
        }
    }
}

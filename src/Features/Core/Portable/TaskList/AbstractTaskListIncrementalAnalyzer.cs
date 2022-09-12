﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.TodoComments;

namespace Microsoft.CodeAnalysis.TaskList
{
    internal abstract partial class AbstractTaskListIncrementalAnalyzer : IncrementalAnalyzerBase
    {
        private readonly object _gate = new();
        private ImmutableArray<string> _lastTokenList = ImmutableArray<string>.Empty;
        private ImmutableArray<TodoCommentDescriptor> _lastDescriptors = ImmutableArray<TodoCommentDescriptor>.Empty;

        /// <summary>
        /// Set of documents that we have reported an non-empty set of todo comments for.  Used so that we don't bother
        /// notifying the host about documents with empty-todo lists (the common case). Note: no locking is needed for
        /// this set as the incremental analyzer is guaranteed to make all calls sequentially to us.
        /// </summary>
        private readonly HashSet<DocumentId> _documentsWithTodoComments = new();

        protected AbstractTaskListIncrementalAnalyzer()
        {
        }

        protected abstract ValueTask ReportTaskListItemsAsync(DocumentId documentId, ImmutableArray<TaskListItem> data, CancellationToken cancellationToken);
        protected abstract ValueTask<TaskListOptions> GetOptionsAsync(CancellationToken cancellationToken);

        public override Task RemoveDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            // Remove the doc id from what we're tracking to prevent unbounded growth in the set.

            // If the doc that is being removed is not in the set of docs we've told the host has todo comments,
            // then no need to notify the host at all about it.
            if (!_documentsWithTodoComments.Remove(documentId))
                return Task.CompletedTask;

            // Otherwise, report that there should now be no todo comments for this doc.
            return ReportTaskListItemsAsync(documentId, ImmutableArray<TaskListItem>.Empty, cancellationToken).AsTask();
        }

        private ImmutableArray<TodoCommentDescriptor> GetTodoCommentDescriptors(ImmutableArray<string> tokenList)
        {
            lock (_gate)
            {
                if (!tokenList.SequenceEqual(_lastTokenList))
                {
                    _lastDescriptors = TodoCommentDescriptor.Parse(tokenList);
                    _lastTokenList = tokenList;
                }

                return _lastDescriptors;
            }
        }

        public override async Task AnalyzeSyntaxAsync(Document document, InvocationReasons reasons, CancellationToken cancellationToken)
        {
            var todoCommentService = document.GetLanguageService<ITodoCommentService>();
            if (todoCommentService == null)
                return;

            var options = await GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var descriptors = GetTodoCommentDescriptors(options.Descriptors);

            // We're out of date.  Recompute this info.
            var todoComments = await todoCommentService.GetTodoCommentsAsync(
                document, descriptors, cancellationToken).ConfigureAwait(false);

            // Convert the roslyn-level results to the more VS oriented line/col data.
            using var _ = ArrayBuilder<TaskListItem>.GetInstance(out var converted);
            await TodoComment.ConvertAsync(
                document, todoComments, converted, cancellationToken).ConfigureAwait(false);

            var data = converted.ToImmutable();
            if (data.IsEmpty)
            {
                // Remove this doc from the set of docs with todo comments in it. If this was a doc that previously
                // had todo comments in it, then fall through and notify the host so it can clear them out.
                // Otherwise, bail out as there's no need to inform the host of this.
                if (!_documentsWithTodoComments.Remove(document.Id))
                    return;
            }
            else
            {
                // Doc has some todo comments, record that, and let the host know.
                _documentsWithTodoComments.Add(document.Id);
            }

            // Now inform VS about this new information
            await ReportTaskListItemsAsync(document.Id, data, cancellationToken).ConfigureAwait(false);
        }
    }
}

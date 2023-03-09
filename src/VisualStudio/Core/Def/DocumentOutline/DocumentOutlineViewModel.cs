﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Roslyn.Utilities;
using VsWebSite;

namespace Microsoft.VisualStudio.LanguageServices.DocumentOutline
{
    using LspDocumentSymbol = DocumentSymbol;

    internal sealed class DocumentOutlineViewState
    {
        /// <summary>
        /// The snapshot of the document used to compute this state.
        /// </summary>
        public readonly ITextSnapshot TextSnapshot;

        /// <summary>
        /// The individual outline items that were computed.
        /// </summary>
        public readonly ImmutableArray<DocumentSymbolData> DocumentSymbolData;

        /// <summary>
        /// The text string that was used to filter the original LSP results down to the set of <see
        /// cref="DocumentSymbolData"/> we have.
        /// </summary>
        public readonly string SearchText;

        /// <summary>
        /// The view items we created from <see cref="DocumentSymbolData"/>.  Note: these values are a bit odd in that
        /// they represent mutable UI state.  Need to review to ensure this is safe.  Docs on
        /// DocumentSymbolDataViewModel indicate that it likely should be as the only mutable state is
        /// IsExpanded/IsSelected, both of which are threadsafe.
        /// </summary>
        public readonly ImmutableArray<DocumentSymbolDataViewModel> ViewModelItems;

        /// <summary>
        /// Interval-tree view over <see cref="ViewModelItems"/> so that we can quickly determine which of them
        /// intersect with a particular position in the document.
        /// </summary>
        public readonly IntervalTree<DocumentSymbolDataViewModel> ViewModelItemsTree;

        public DocumentOutlineViewState(
            ITextSnapshot textSnapshot,
            ImmutableArray<DocumentSymbolData> documentSymbolData,
            string searchText,
            ImmutableArray<DocumentSymbolDataViewModel> viewModelItems,
            IntervalTree<DocumentSymbolDataViewModel> viewModelItemsTree)
        {
            TextSnapshot = textSnapshot;
            DocumentSymbolData = documentSymbolData;
            SearchText = searchText;
            ViewModelItems = viewModelItems;
            ViewModelItemsTree = viewModelItemsTree;
        }
    }

    internal readonly struct DocumentOutlineViewModelIntervalInspector : IIntervalIntrospector<DocumentSymbolDataViewModel>
    {
        private readonly ITextSnapshot _textSnapshot;

        public DocumentOutlineViewModelIntervalInspector(ITextSnapshot textSnapshot)
        {
            _textSnapshot = textSnapshot;
        }

        public int GetStart(DocumentSymbolDataViewModel value)
        {
            return value.Data.RangeSpan.Start.TranslateTo(_textSnapshot, PointTrackingMode.Positive);
        }

        public int GetLength(DocumentSymbolDataViewModel value)
        {
            return value.Data.RangeSpan.TranslateTo(_textSnapshot, SpanTrackingMode.EdgeInclusive).Length;
        }
    }

    /// <summary>
    /// Responsible for updating data related to Document outline. It is expected that all public methods on this type
    /// do not need to be on the UI thread. Two properties: <see cref="SortOption"/> and <see cref="SearchText"/> are
    /// intended to be bound to a WPF view and should only be set from the UI thread.
    /// </summary>
    internal sealed partial class DocumentOutlineViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ILanguageServiceBroker2 _languageServiceBroker;
        private readonly ITaggerEventSource _taggerEventSource;
        private readonly ITextView _textView;
        private readonly ITextBuffer _textBuffer;
        private readonly IThreadingContext _threadingContext;
        private readonly CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Queue that uses the language-server-protocol to get document symbol information.
        /// </summary>
        private readonly AsyncBatchingWorkQueue _workQueue;

        public event PropertyChangedEventHandler? PropertyChanged;

        ///// <summary>
        ///// Queue for updating the state of the view model.  The boolean indicates if we should expand/collapse all
        ///// items.
        ///// </summary>
        //private readonly AsyncBatchingWorkQueue<bool?> _updateViewModelStateQueue;

        private CancellationToken CancellationToken => _cancellationTokenSource.Token;

        // Mutable state.  Should only update on UI thread.

        private SortOption _sortOption_doNotAccessDirectly = SortOption.Location;
        private string _searchText_doNotAccessDirectly = "";
        private ImmutableArray<DocumentSymbolDataViewModel> _documentSymbolViewModelItems_doNotAccessDirectly = ImmutableArray<DocumentSymbolDataViewModel>.Empty;

        /// <summary>
        /// Mutable state.  only accessed from UpdateViewModelStateAsync though.  Since that executes serially, it does not need locking.
        /// </summary>
        private DocumentOutlineViewState _lastViewState;

        public DocumentOutlineViewModel(
            ILanguageServiceBroker2 languageServiceBroker,
            IAsynchronousOperationListener asyncListener,
            ITaggerEventSource taggerEventSource,
            ITextView textView,
            ITextBuffer textBuffer,
            IThreadingContext threadingContext)
        {
            _languageServiceBroker = languageServiceBroker;
            _taggerEventSource = taggerEventSource;
            _textView = textView;
            _textBuffer = textBuffer;
            _threadingContext = threadingContext;

            var currentSnapshot = textBuffer.CurrentSnapshot;
            _lastViewState = new DocumentOutlineViewState(
                currentSnapshot,
                ImmutableArray<DocumentSymbolData>.Empty,
                this.SearchText,
                this.DocumentSymbolViewModelItems,
                IntervalTree<DocumentSymbolDataViewModel>.Empty);

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_threadingContext.DisposalToken);

            //// work queue for refreshing LSP data
            _workQueue = new AsyncBatchingWorkQueue(
                DelayTimeSpan.Short,
                ComputeViewStateAsync,
                asyncListener,
                CancellationToken);
            //_documentSymbolQueue = new AsyncBatchingResultQueue<DocumentSymbolDataModel>(
            //    DelayTimeSpan.Short,
            //    GetDocumentSymbolAsync,
            //    asyncListener,
            //    CancellationToken);

            //// work queue for updating UI state
            //_updateViewModelStateQueue = new AsyncBatchingWorkQueue<bool?>(
            //    DelayTimeSpan.Short,
            //    UpdateViewModelStateAsync,
            //    asyncListener,
            //    CancellationToken);

            _taggerEventSource.Changed += OnEventSourceChanged;
            _taggerEventSource.Connect();

            // queue initial model update
            _workQueue.AddWork();
        }

        public void Dispose()
        {
            _taggerEventSource.Changed -= OnEventSourceChanged;
            _taggerEventSource.Disconnect();
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }

        public SortOption SortOption
        {
            get
            {
                _threadingContext.ThrowIfNotOnUIThread();
                return _sortOption_doNotAccessDirectly;
            }

            set
            {
                // Called from WPF.

                _threadingContext.ThrowIfNotOnUIThread();
                SetProperty(ref _sortOption_doNotAccessDirectly, value);

                // We do not need to update our views here.  Sorting is handled entirely by WPF using
                // DocumentSymbolDataViewModelSorter.
            }
        }

        public string SearchText
        {
            get
            {
                _threadingContext.ThrowIfNotOnUIThread();
                return _searchText_doNotAccessDirectly;
            }

            set
            {
                // setting this happens from wpf itself.  So once this changes, kick off the work to actually filter down our models.

                _threadingContext.ThrowIfNotOnUIThread();
                _searchText_doNotAccessDirectly = value;

                _workQueue.AddWork();
            }
        }

        public ImmutableArray<DocumentSymbolDataViewModel> DocumentSymbolViewModelItems
        {
            get
            {
                _threadingContext.ThrowIfNotOnUIThread();
                return _documentSymbolViewModelItems_doNotAccessDirectly;
            }

            // Setting this only happens from within this type once we've computed new items or filtered down the existing set.
            private set
            {
                _threadingContext.ThrowIfNotOnBackgroundThread();

                // Unselect any currently selected items or WPF will believe it needs to select the root node.
                UnselectAll(_documentSymbolViewModelItems_doNotAccessDirectly);
                SetProperty(ref _documentSymbolViewModelItems_doNotAccessDirectly, value);
            }
        }

        private void OnEventSourceChanged(object sender, TaggerEventArgs e)
            => _workQueue.AddWork();

        private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            NotifyPropertyChanged(propertyName);
        }

        public void EnqueueExpandOrCollapse(bool shouldExpand)
            => _updateViewModelStateQueue.AddWork(shouldExpand);

        private async ValueTask ComputeViewStateAsync(CancellationToken cancellationToken)
        {
            // We do not want this work running on a background thread
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            var textBuffer = _textBuffer;

            var filePath = textBuffer.GetRelatedDocuments().FirstOrDefault(static d => d.FilePath is not null)?.FilePath;
            if (filePath is null)
            {
                // text buffer is not saved to disk. LSP does not support calls without URIs. and Visual Studio does not
                // have a URI concept other than the file path.
                return;
            }

            // Obtain the LSP response and text snapshot used.
            var response = await DocumentSymbolsRequestAsync(
                textBuffer, _languageServiceBroker, filePath, cancellationToken).ConfigureAwait(false);
            if (response is null)
                return;

            var responseBody = response.Value.response.ToObject<LspDocumentSymbol[]>() ?? Array.Empty<LspDocumentSymbol>();
            var model = CreateDocumentSymbolDataModel(responseBody, response.Value.snapshot);

            // Now that we produced a new model, kick off the work to present it to the UI.
            _updateViewModelStateQueue.AddWork(item: null);

            return model;
        }

        private async ValueTask UpdateViewModelStateAsync(ImmutableSegmentedList<bool?> viewModelStateData, CancellationToken cancellationToken)
        {
            // just to UI thread to get the last UI state we presented.
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var searchText = this.SearchText;
            var caretPoint = _textView.Caret.Position.BufferPosition;
            var lastPresentedData = _lastPresentedData_onlyAccessSerially;

            // Jump back to the BG to do all our work.
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            // Grab the last computed model.  We can compare it to what we previously presented to see if it's changed.
            var model = await _documentSymbolQueue.WaitUntilCurrentBatchCompletesAsync().ConfigureAwait(false) ?? _emptyModel;

            var expansion = viewModelStateData.LastOrDefault(b => b != null);

            var modelChanged = model != lastPresentedData.model;
            var searchTextChanged = searchText != lastPresentedData.searchText;
            var lastViewModelItems = lastPresentedData.viewModelItems;

            ImmutableArray<DocumentSymbolDataViewModel> currentViewModelItems;

            // if we got new data or the user changed the search text, recompute our items to correspond to this new state.
            if (modelChanged || searchTextChanged)
            {
                // Apply whatever the current search text is to what the model returned, and produce the new items.
                currentViewModelItems = GetDocumentSymbolItemViewModels(
                    SearchDocumentSymbolData(model.DocumentSymbolData, searchText, cancellationToken));

                // If the search text changed, just show everything in expanded form, so the user can see everything
                // that matched, without anything being hidden.
                //
                // in the case of no search text change, attempt to keep the same open/close expansion state from before.
                if (!searchTextChanged)
                {
                    ApplyExpansionStateToNewItems(
                        oldSnapshot: lastPresentedData.model.OriginalSnapshot,
                        newSnapshot: model.OriginalSnapshot,
                        oldItems: lastViewModelItems,
                        newItems: currentViewModelItems);
                }
            }
            else
            {
                // Model didn't change and search text didn't change.  Keep what we have, and only figure out what to
                // select/expand below.
                currentViewModelItems = lastViewModelItems;
            }

            // If we aren't filtering to search results do expand/collapse
            if (expansion != null)
                SetExpansionOption(currentViewModelItems, expansion.Value);

            // If we produced new items, then let wpf know so it can update hte UI.
            if (currentViewModelItems != lastViewModelItems)
                this.DocumentSymbolViewModelItems = currentViewModelItems;

            // Now that we've made all our changes, record that we've done so so we can see what has changed when future requests come in.
            // note: we are safe to record this on the BG as we are called serially and are the only place to read/write it.

            // Jump back to UI thread to set the current data.
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            _lastPresentedData_onlyAccessSerially = (model, searchText, currentViewModelItems);

            return;

            static void ApplyExpansionStateToNewItems(
                ITextSnapshot oldSnapshot,
                ITextSnapshot newSnapshot,
                ImmutableArray<DocumentSymbolDataViewModel> oldItems,
                ImmutableArray<DocumentSymbolDataViewModel> newItems)
            {
                // If we had any items from before, and they were all collapsed, the collapse all the new items.
                if (oldItems.Length > 0 && oldItems.All(static i => !i.IsExpanded))
                {
                    // new nodes are un-collapsed by default
                    // we want to collapse all new top-level nodes if 
                    // everything else currently is so things aren't "jumpy"
                    foreach (var item in newItems)
                        item.IsExpanded = false;

                    return;
                }

                // Walk through the old items, mapping their spans forward and keeping track if they were expanded or
                // collapsed.  Then walk through the new items and see if they have the same span as a prior item.  If
                // so, preserve the expansion state.
                using var _ = PooledDictionary<Span, bool>.GetInstance(out var expansionState);
                AddPreviousExpansionState(newSnapshot, oldItems, expansionState);
                ApplyExpansionState(expansionState, newItems);
            }

            static void AddPreviousExpansionState(
                ITextSnapshot newSnapshot,
                ImmutableArray<DocumentSymbolDataViewModel> oldItems,
                PooledDictionary<Span, bool> expansionState)
            {
                foreach (var item in oldItems)
                {
                    // EdgeInclusive so that if we type on the end of an existing item it maps forward to the new full span.
                    var mapped = item.Data.SelectionRangeSpan.TranslateTo(newSnapshot, SpanTrackingMode.EdgeInclusive);
                    expansionState[mapped.Span] = item.IsExpanded;

                    AddPreviousExpansionState(newSnapshot, item.Children, expansionState);
                }
            }

            static void ApplyExpansionState(
                PooledDictionary<Span, bool> expansionState,
                ImmutableArray<DocumentSymbolDataViewModel> newItems)
            {
                foreach (var item in newItems)
                {
                    if (expansionState.TryGetValue(item.Data.SelectionRangeSpan.Span, out var isExpanded))
                        item.IsExpanded = isExpanded;

                    ApplyExpansionState(expansionState, item.Children);
                }
            }
        }

        /// <summary>
        /// Updates the IsExpanded property for the Document Symbol ViewModel based on the given Expansion Option. The parameter
        /// <param name="currentDocumentSymbolItems"/> is used to reference the current node expansion in the view.
        /// </summary>
        public static void SetIsExpandedOnNewItems(
            ImmutableArray<DocumentSymbolDataViewModel> newDocumentSymbolItems,
            ImmutableArray<DocumentSymbolDataViewModel> currentDocumentSymbolItems)
        {
            using var _ = PooledHashSet<DocumentSymbolDataViewModel>.GetInstance(out var hashSet);
            hashSet.AddRange(newDocumentSymbolItems);

            foreach (var item in currentDocumentSymbolItems)
            {
                if (!hashSet.TryGetValue(item, out var newItem))
                {
                    continue;
                }

                // Setting a boolean property on this View Model is allowed to happen on any thread.
                newItem.IsExpanded = item.IsExpanded;
                SetIsExpandedOnNewItems(newItem.Children, item.Children);
            }
        }
    }
}

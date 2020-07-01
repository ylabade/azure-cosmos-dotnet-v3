﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Pagination
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Microsoft.Azure.Cosmos.Query.Core.Collections;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;

    /// <summary>
    /// Coordinates draining pages from multiple <see cref="PartitionRangePageEnumerator{TPage, TState}"/>, while maintaining a global sort order and handling repartitioning (splits, merge).
    /// </summary>
    internal abstract class CrossPartitionRangePageEnumerator<TPage, TState> : IAsyncEnumerator<TryCatch<CrossPartitionPage<TPage, TState>>>
        where TPage : Page<TState>
        where TState : State
    {
        private readonly IFeedRangeProvider feedRangeProvider;
        private readonly CreatePartitionRangePageEnumerator<TPage, TState> createPartitionRangeEnumerator;
        private readonly AsyncLazy<PriorityQueue<PartitionRangePageEnumerator<TPage, TState>>> lazyEnumerators;

        public CrossPartitionRangePageEnumerator(
            IFeedRangeProvider feedRangeProvider,
            CreatePartitionRangePageEnumerator<TPage, TState> createPartitionRangeEnumerator,
            IComparer<PartitionRangePageEnumerator<TPage, TState>> comparer,
            CrossPartitionState<TState> state = default)
        {
            this.feedRangeProvider = feedRangeProvider ?? throw new ArgumentNullException(nameof(feedRangeProvider));
            this.createPartitionRangeEnumerator = createPartitionRangeEnumerator ?? throw new ArgumentNullException(nameof(createPartitionRangeEnumerator));

            if (comparer == null)
            {
                throw new ArgumentNullException(nameof(comparer));
            }

            this.lazyEnumerators = new AsyncLazy<PriorityQueue<PartitionRangePageEnumerator<TPage, TState>>>(async (CancellationToken token) =>
            {
                IReadOnlyList<(FeedRange, TState)> rangeAndStates;
                if (state != default)
                {
                    rangeAndStates = state.Value;
                }
                else
                {
                    // Fan out to all partitions with default state
                    IEnumerable<FeedRange> ranges = await feedRangeProvider.GetFeedRangesAsync(token);

                    List<(FeedRange, TState)> rangesAndStatesBuilder = new List<(FeedRange, TState)>();
                    foreach (FeedRange range in ranges)
                    {
                        rangesAndStatesBuilder.Add((range, default));
                    }

                    rangeAndStates = rangesAndStatesBuilder;
                }

                PriorityQueue<PartitionRangePageEnumerator<TPage, TState>> enumerators = new PriorityQueue<PartitionRangePageEnumerator<TPage, TState>>(comparer);
                foreach ((FeedRange range, TState rangeState) in rangeAndStates)
                {
                    PartitionRangePageEnumerator<TPage, TState> enumerator = createPartitionRangeEnumerator(range, rangeState);
                    enumerators.Enqueue(enumerator);
                }

                return enumerators;
            });
        }

        public TryCatch<CrossPartitionPage<TPage, TState>> Current { get; private set; }

        public async ValueTask<bool> MoveNextAsync()
        {
            PriorityQueue<PartitionRangePageEnumerator<TPage, TState>> enumerators = await this.lazyEnumerators.GetValueAsync(cancellationToken: default);
            PartitionRangePageEnumerator<TPage, TState> currentPaginator = enumerators.Dequeue();
            bool movedNext = await currentPaginator.MoveNextAsync();
            if (!movedNext)
            {
                return false;
            }

            if (currentPaginator.Current.Failed)
            {
                // Check if it's a retryable exception.
                Exception exception = currentPaginator.Current.Exception;
                while (exception.InnerException != null)
                {
                    exception = exception.InnerException;
                }

                if (IsSplitException(exception))
                {
                    // Handle split
                    IEnumerable<FeedRange> childRanges = await this.feedRangeProvider.GetChildRangeAsync(currentPaginator.Range);
                    foreach (FeedRange childRange in childRanges)
                    {
                        PartitionRangePageEnumerator<TPage, TState> childPaginator = this.createPartitionRangeEnumerator(childRange, currentPaginator.State);
                        enumerators.Enqueue(childPaginator);
                    }

                    // Recursively retry
                    return await this.MoveNextAsync();
                }

                if (IsMergeException(exception))
                {
                    throw new NotImplementedException();
                }
            }

            enumerators.Enqueue(currentPaginator);

            TryCatch<TPage> backendPage = currentPaginator.Current;
            if (backendPage.Failed)
            {
                this.Current = TryCatch<CrossPartitionPage<TPage, TState>>.FromException(backendPage.Exception);
                return true;
            }

            List<(FeedRange, TState)> feedRangeAndStates = new List<(FeedRange, TState)>(enumerators.Count);
            foreach (PartitionRangePageEnumerator<TPage, TState> enumerator in enumerators)
            {
                feedRangeAndStates.Add((enumerator.Range, enumerator.State));
            }

            CrossPartitionState<TState> crossPartitionState = new CrossPartitionState<TState>(feedRangeAndStates);
            this.Current = TryCatch<CrossPartitionPage<TPage, TState>>.FromResult(
                new CrossPartitionPage<TPage, TState>(backendPage.Result, crossPartitionState));
            return true;
        }

        public ValueTask DisposeAsync()
        {
            // Do Nothing.
            return default;
        }

        private static bool IsSplitException(Exception exeception)
        {
            return exeception is CosmosException cosmosException
                && cosmosException.StatusCode == HttpStatusCode.Gone
                && cosmosException.SubStatusCode == (int)Documents.SubStatusCodes.PartitionKeyRangeGone;
        }

        private static bool IsMergeException(Exception exception)
        {
            // TODO: code this out
            return false;
        }
    }
}

﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Collections;

public sealed class IntervalTreeTests
{
    private readonly struct TupleIntrospector<T> : IIntervalIntrospector<Tuple<int, int, T>>
    {
        public TextSpan GetSpan(Tuple<int, int, T> value)
            => new(value.Item1, value.Item2);
    }

    private static IEnumerable<SimpleIntervalTree<Tuple<int, int, string>, TupleIntrospector<string>>> CreateTrees(params Tuple<int, int, string>[] values)
        => CreateTrees((IEnumerable<Tuple<int, int, string>>)values);

    private static IEnumerable<SimpleIntervalTree<Tuple<int, int, string>, TupleIntrospector<string>>> CreateTrees(IEnumerable<Tuple<int, int, string>> values)
    {
        yield return SimpleIntervalTree.Create(new TupleIntrospector<string>(), values);
    }

    [Fact]
    public void TestEmpty()
    {
        foreach (var tree in CreateTrees())
        {
            var spans = tree.GetIntervalsThatOverlapWith(0, 1);

            Assert.Empty(spans);
        }
    }

    [Fact]
    public void TestBeforeSpan()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(0, 1);

            Assert.Empty(spans);
        }
    }

    [Fact]
    public void TestAbuttingBeforeSpan()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(0, 5);

            Assert.Empty(spans);
        }
    }

    [Fact]
    public void TestAfterSpan()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(15, 5);

            Assert.Empty(spans);
        }
    }

    [Fact]
    public void TestAbuttingAfterSpan()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(10, 5);

            Assert.Empty(spans);
        }
    }

    [Fact]
    public void TestMatchingSpan()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(5, 5).Select(t => t.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestContainedAbuttingStart()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(5, 2).Select(i => i.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestContainedAbuttingEnd()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(8, 2).Select(i => i.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestCompletedContained()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(7, 2).Select(i => i.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestOverlappingStart()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(4, 2).Select(i => i.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestOverlappingEnd()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(9, 2).Select(i => i.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestOverlappingAll()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A")))
        {
            var spans = tree.GetIntervalsThatOverlapWith(4, 7).Select(i => i.Item3);

            Assert.True(Set("A").SetEquals(spans));
        }
    }

    [Fact]
    public void TestNonOverlappingSpans()
    {
        foreach (var tree in CreateTrees(Tuple.Create(5, 5, "A"), Tuple.Create(15, 5, "B")))
        {
            // Test between the spans
            Assert.Empty(tree.GetIntervalsThatOverlapWith(2, 2));
            Assert.Empty(tree.GetIntervalsThatOverlapWith(11, 2));
            Assert.Empty(tree.GetIntervalsThatOverlapWith(22, 2));

            // Test in the spans
            Assert.True(Set("A").SetEquals(tree.GetIntervalsThatOverlapWith(6, 2).Select(i => i.Item3)));
            Assert.True(Set("B").SetEquals(tree.GetIntervalsThatOverlapWith(16, 2).Select(i => i.Item3)));

            // Test covering both spans
            Assert.True(Set("A", "B").SetEquals(tree.GetIntervalsThatOverlapWith(2, 20).Select(i => i.Item3)));
            Assert.True(Set("A", "B").SetEquals(tree.GetIntervalsThatOverlapWith(2, 14).Select(i => i.Item3)));
            Assert.True(Set("A", "B").SetEquals(tree.GetIntervalsThatOverlapWith(6, 10).Select(i => i.Item3)));
            Assert.True(Set("A", "B").SetEquals(tree.GetIntervalsThatOverlapWith(6, 20).Select(i => i.Item3)));
        }
    }

    [Fact]
    public void TestSubsumedSpans()
    {
        var spans = List(
            Tuple.Create(5, 5, "a"),
            Tuple.Create(6, 3, "b"),
            Tuple.Create(7, 1, "c"));

        TestOverlapsAndIntersects(spans);
    }

    [Fact]
    public void TestOverlappingSpans()
    {
        var spans = List(
            Tuple.Create(5, 5, "a"),
            Tuple.Create(7, 5, "b"),
            Tuple.Create(9, 5, "c"));

        TestOverlapsAndIntersects(spans);
    }

    [Fact]
    public void TestIntersectsWith()
    {
        var spans = List(
            Tuple.Create(0, 2, "a"));

        foreach (var tree in CreateTrees(spans))
        {
            Assert.False(tree.HasIntervalThatIntersectsWith(-1));
            Assert.True(tree.HasIntervalThatIntersectsWith(0));
            Assert.True(tree.HasIntervalThatIntersectsWith(1));
            Assert.True(tree.HasIntervalThatIntersectsWith(2));
            Assert.False(tree.HasIntervalThatIntersectsWith(3));
        }
    }

    [Fact]
    public void LargeTest()
    {
        var spans = List(
            Tuple.Create(0, 3, "a"),
            Tuple.Create(5, 3, "b"),
            Tuple.Create(6, 4, "c"),
            Tuple.Create(8, 1, "d"),
            Tuple.Create(15, 8, "e"),
            Tuple.Create(16, 5, "f"),
            Tuple.Create(17, 2, "g"),
            Tuple.Create(19, 1, "h"),
            Tuple.Create(25, 5, "i"));

        TestOverlapsAndIntersects(spans);
    }

    [Fact]
    public void TestCrash1()
    {
        foreach (var _ in CreateTrees(Tuple.Create(8, 1, "A"), Tuple.Create(59, 1, "B"), Tuple.Create(52, 1, "C")))
        {
        }
    }

    [Fact]
    public void TestEmptySpanAtStart()
    {
        // Make sure creating empty spans works (there was a bug here)
        var tree = CreateTrees(Tuple.Create(0, 0, "A")).Last();

        Assert.Equal(1, tree.Count());
    }

    private readonly struct Int32Introspector : IIntervalIntrospector<int>
    {
        public TextSpan GetSpan(int value)
            => new(value, 0);
    }

    private static IntervalTree<int> CreateIntTree(params int[] values)
        => IntervalTree<int>.Create(new Int32Introspector(), values);

    [Fact]
    public void TestSortedEnumerable1()
    {
        Assert.Equal(CreateIntTree(0, 0, 0), new[] { 0, 0, 0 });
        Assert.Equal(CreateIntTree(0, 0, 1), new[] { 0, 0, 1 });
        Assert.Equal(CreateIntTree(0, 0, 2), new[] { 0, 0, 2 });
        Assert.Equal(CreateIntTree(0, 1, 0), new[] { 0, 0, 1 });
        Assert.Equal(CreateIntTree(0, 1, 1), new[] { 0, 1, 1 });
        Assert.Equal(CreateIntTree(0, 1, 2), new[] { 0, 1, 2 });
        Assert.Equal(CreateIntTree(0, 2, 0), new[] { 0, 0, 2 });
        Assert.Equal(CreateIntTree(0, 2, 1), new[] { 0, 1, 2 });
        Assert.Equal(CreateIntTree(0, 2, 2), new[] { 0, 2, 2 });

        Assert.Equal(CreateIntTree(1, 0, 0), new[] { 0, 0, 1 });
        Assert.Equal(CreateIntTree(1, 0, 1), new[] { 0, 1, 1 });
        Assert.Equal(CreateIntTree(1, 0, 2), new[] { 0, 1, 2 });
        Assert.Equal(CreateIntTree(1, 1, 0), new[] { 0, 1, 1 });
        Assert.Equal(CreateIntTree(1, 1, 1), new[] { 1, 1, 1 });
        Assert.Equal(CreateIntTree(1, 1, 2), new[] { 1, 1, 2 });
        Assert.Equal(CreateIntTree(1, 2, 0), new[] { 0, 1, 2 });
        Assert.Equal(CreateIntTree(1, 2, 1), new[] { 1, 1, 2 });
        Assert.Equal(CreateIntTree(1, 2, 2), new[] { 1, 2, 2 });

        Assert.Equal(CreateIntTree(2, 0, 0), new[] { 0, 0, 2 });
        Assert.Equal(CreateIntTree(2, 0, 1), new[] { 0, 1, 2 });
        Assert.Equal(CreateIntTree(2, 0, 2), new[] { 0, 2, 2 });
        Assert.Equal(CreateIntTree(2, 1, 0), new[] { 0, 1, 2 });
        Assert.Equal(CreateIntTree(2, 1, 1), new[] { 1, 1, 2 });
        Assert.Equal(CreateIntTree(2, 1, 2), new[] { 1, 2, 2 });
        Assert.Equal(CreateIntTree(2, 2, 0), new[] { 0, 2, 2 });
        Assert.Equal(CreateIntTree(2, 2, 1), new[] { 1, 2, 2 });
        Assert.Equal(CreateIntTree(2, 2, 2), new[] { 2, 2, 2 });
    }

    [Fact]
    public void TestSortedEnumerable2()
    {
        var tree = IntervalTree<int>.Create(new Int32Introspector(), new[] { 1, 0 });

        Assert.Equal(tree, new[] { 0, 1 });
    }

    private static void TestOverlapsAndIntersects(IList<Tuple<int, int, string>> spans)
    {
        foreach (var tree in CreateTrees(spans))
        {
            var max = spans.Max(t => t.Item1 + t.Item2);
            for (var start = 0; start <= max; start++)
            {
                for (var length = 1; length <= max; length++)
                {
                    var span = new Span(start, length);

                    var set1 = new HashSet<string>(tree.GetIntervalsThatOverlapWith(start, length).Select(i => i.Item3));
                    var set2 = new HashSet<string>(spans.Where(t =>
                    {
                        return span.OverlapsWith(new Span(t.Item1, t.Item2));
                    }).Select(t => t.Item3));
                    Assert.True(set1.SetEquals(set2));

                    var set3 = new HashSet<string>(tree.GetIntervalsThatIntersectWith(start, length).Select(i => i.Item3));
                    var set4 = new HashSet<string>(spans.Where(t =>
                    {
                        return span.IntersectsWith(new Span(t.Item1, t.Item2));
                    }).Select(t => t.Item3));
                    Assert.True(set3.SetEquals(set4));
                }
            }

            Assert.Equal(spans.Count, tree.Count());
            Assert.True(new HashSet<string>(spans.Select(t => t.Item3)).SetEquals(tree.Select(i => i.Item3)));
        }
    }

    private static ISet<T> Set<T>(params T[] values)
        => new HashSet<T>(values);

    private static IList<T> List<T>(params T[] values)
        => new List<T>(values);
}

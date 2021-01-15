﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CodeFixes.FullyQualify
{
    internal abstract partial class AbstractFullyQualifyCodeFixProvider : CodeFixProvider
    {
        private readonly struct SymbolResult : IEquatable<SymbolResult>, IComparable<SymbolResult>
        {
            public readonly INamespaceOrTypeSymbol Symbol;
            public readonly int Weight;
            public readonly IReadOnlyList<string> NameParts;

            /// <summary>
            /// Whether the original symbol at the time of creation was a type. This is unchanged by
            /// the <see cref="WithSymbol(INamespaceOrTypeSymbol)"/> method and doesn't affect comparisons.
            /// </summary>
            public readonly bool OriginalSymbolIsType;

            public SymbolResult(INamespaceOrTypeSymbol symbol, int weight, bool originalSymbolIsType)
            {
                Symbol = symbol;
                Weight = weight;
                NameParts = INamespaceOrTypeSymbolExtensions.GetNameParts(symbol);
                OriginalSymbolIsType = originalSymbolIsType;
            }

            public override bool Equals(object? obj)
                => Equals((SymbolResult)obj);

            public bool Equals(SymbolResult other)
                => Equals(Symbol, other.Symbol);

            public override int GetHashCode()
                => Symbol.GetHashCode();

            public SymbolResult WithSymbol(INamespaceOrTypeSymbol other)
                => new(other, Weight, OriginalSymbolIsType);

            public int CompareTo(SymbolResult other)
            {
                Debug.Assert(Symbol is INamespaceSymbol || !((INamedTypeSymbol)Symbol).IsGenericType);
                Debug.Assert(other.Symbol is INamespaceSymbol || !((INamedTypeSymbol)other.Symbol).IsGenericType);

                var diff = Weight - other.Weight;
                if (diff != 0)
                {
                    return diff;
                }

                return INamespaceOrTypeSymbolExtensions.CompareNameParts(
                    NameParts, other.NameParts, placeSystemNamespaceFirst: true);
            }
        }
    }
}

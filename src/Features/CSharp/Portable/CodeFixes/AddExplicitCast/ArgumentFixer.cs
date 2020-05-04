﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.CodeFixes.AddExplicitCast
{
    internal sealed partial class CSharpAddExplicitCastCodeFixProvider
    {
        private class ArgumentFixer : Fixer<ArgumentSyntax, ArgumentListSyntax, SyntaxNode>
        {
            public ArgumentFixer(CSharpAddExplicitCastCodeFixProvider provider) : base(provider)
            {
            }

            protected override ArgumentSyntax GenerateNewArgument(ArgumentSyntax oldArgument, ITypeSymbol conversionType)
                => oldArgument.WithExpression(oldArgument.Expression.Cast(conversionType));

            protected override ArgumentListSyntax GenerateNewArgumentList(ArgumentListSyntax oldArgumentList, List<ArgumentSyntax> newArguments)
                => oldArgumentList.WithArguments(SyntaxFactory.SeparatedList(newArguments));

            protected override SeparatedSyntaxList<ArgumentSyntax> GetArgumentsOfArgumentList(ArgumentListSyntax argumentList)
                => argumentList.Arguments;

            protected override SymbolInfo GetSpeculativeSymbolInfo(SemanticModel semanticModel, ArgumentListSyntax newArgumentList)
            {
                var newInvocation = newArgumentList.Parent!;
                return semanticModel.GetSpeculativeSymbolInfo(newInvocation.SpanStart, newInvocation, SpeculativeBindingOption.BindAsExpression);
            }
        }
    }
}

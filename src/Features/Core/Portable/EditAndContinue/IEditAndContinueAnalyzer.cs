﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
namespace Microsoft.CodeAnalysis.EditAndContinue
{
    internal interface IEditAndContinueAnalyzer : ILanguageService
    {
        Task<DocumentAnalysisResults> AnalyzeDocumentAsync(Project baseProject, ImmutableArray<ActiveStatement> baseActiveStatements, Document document, ImmutableArray<TextSpan> newActiveStatementSpans, EditAndContinueCapabilities capabilities, CancellationToken cancellationToken);
        ImmutableArray<LinePositionSpan> GetExceptionRegions(SourceText text, SyntaxNode syntaxRoot, LinePositionSpan activeStatementSpan, bool isLeaf, out bool isCovered);
    }
}

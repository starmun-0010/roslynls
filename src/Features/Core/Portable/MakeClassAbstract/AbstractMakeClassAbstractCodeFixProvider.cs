﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.MakeClassAbstract
{
    internal abstract class AbstractMakeClassAbstractCodeFixProvider<TClassDeclarationSyntax> : SyntaxEditorBasedCodeFixProvider
        where TClassDeclarationSyntax : SyntaxNode
    {
        internal sealed override CodeFixCategory CodeFixCategory => CodeFixCategory.Compile;

        public sealed override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            if (context.Diagnostics.Length == 1 &&
                IsValidMemberNode(context.Diagnostics[0].Location?.FindNode(context.CancellationToken)))
            {
                context.RegisterCodeFix(
                    new MyCodeAction(c => FixAsync(context.Document, context.Diagnostics[0], c)),
                    context.Diagnostics);
            }

            return Task.CompletedTask;
        }

        protected sealed override Task FixAllAsync(Document document, ImmutableArray<Diagnostic> diagnostics, SyntaxEditor editor,
            CancellationToken cancellationToken)
        {
            for (var i = 0; i < diagnostics.Length; i++)
            {
                var declaration = diagnostics[i].Location?.FindNode(cancellationToken);

                if (IsValidMemberNode(declaration))
                {
                    var enclosingClass = declaration.FirstAncestorOrSelf<TClassDeclarationSyntax>();
                    editor.ReplaceNode(enclosingClass,
                        (currentClassDeclaration, generator) => generator.WithModifiers(currentClassDeclaration, DeclarationModifiers.Abstract));
                }
            }

            return Task.CompletedTask;
        }

        protected abstract bool IsValidMemberNode(SyntaxNode node);

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(FeaturesResources.Make_class_abstract, createChangedDocument, nameof(AbstractMakeClassAbstractCodeFixProvider<TClassDeclarationSyntax>))
            {
            }
        }
    }
}

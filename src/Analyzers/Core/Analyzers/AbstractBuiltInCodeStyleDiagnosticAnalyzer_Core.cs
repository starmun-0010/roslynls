﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeStyle
{
    internal abstract partial class AbstractBuiltInCodeStyleDiagnosticAnalyzer : DiagnosticAnalyzer, IBuiltInAnalyzer
    {
        protected readonly DiagnosticDescriptor Descriptor;
        private DiagnosticSeverity? _minimumReportedSeverity;

        private AbstractBuiltInCodeStyleDiagnosticAnalyzer(
            string descriptorId,
            EnforceOnBuild enforceOnBuild,
            LocalizableString title,
            LocalizableString? messageFormat,
            bool isUnnecessary,
            bool configurable,
            bool hasAnyCodeStyleOption)
        {
            // 'isUnnecessary' should be true only for sub-types of AbstractBuiltInUnnecessaryCodeStyleDiagnosticAnalyzer.
            Debug.Assert(!isUnnecessary || this is AbstractBuiltInUnnecessaryCodeStyleDiagnosticAnalyzer);

            Descriptor = CreateDescriptorWithId(descriptorId, enforceOnBuild, hasAnyCodeStyleOption, title, messageFormat ?? title, isUnnecessary: isUnnecessary, isConfigurable: configurable);
            SupportedDiagnostics = ImmutableArray.Create(Descriptor);
        }

        /// <summary>
        /// Constructor for a code style analyzer with a multiple diagnostic descriptors such that all the descriptors have no unique code style option to configure the descriptors.
        /// </summary>
        protected AbstractBuiltInCodeStyleDiagnosticAnalyzer(ImmutableArray<DiagnosticDescriptor> supportedDiagnostics)
        {
            SupportedDiagnostics = supportedDiagnostics;

            Descriptor = SupportedDiagnostics[0];
            Debug.Assert(!supportedDiagnostics.Any(descriptor => descriptor.CustomTags.Any(t => t == WellKnownDiagnosticTags.Unnecessary)) || this is AbstractBuiltInUnnecessaryCodeStyleDiagnosticAnalyzer);
        }

        public virtual bool IsHighPriority => false;
        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }

        protected static DiagnosticDescriptor CreateDescriptorWithId(
            string id,
            EnforceOnBuild enforceOnBuild,
            bool hasAnyCodeStyleOption,
            LocalizableString title,
            LocalizableString? messageFormat = null,
            bool isUnnecessary = false,
            bool isConfigurable = true,
            LocalizableString? description = null)
#pragma warning disable RS0030 // Do not used banned APIs
            => new(
                    id, title, messageFormat ?? title,
                    DiagnosticCategory.Style,
                    DiagnosticSeverity.Hidden,
                    isEnabledByDefault: true,
                    description: description,
                    helpLinkUri: DiagnosticHelper.GetHelpLinkForDiagnosticId(id),
                    customTags: DiagnosticCustomTags.Create(isUnnecessary, isConfigurable, isCustomConfigurable: hasAnyCodeStyleOption, enforceOnBuild));
#pragma warning restore RS0030 // Do not used banned APIs

        /// <summary>
        /// Flags to configure the analysis of generated code.
        /// By default, code style analyzers should not analyze or report diagnostics on generated code, so the value is false.
        /// </summary>
        protected virtual GeneratedCodeAnalysisFlags GeneratedCodeAnalysisFlags => GeneratedCodeAnalysisFlags.None;

        public sealed override void Initialize(AnalysisContext context)
        {
            _minimumReportedSeverity = context.MinimumReportedSeverity;

            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags);
            context.EnableConcurrentExecution();

            InitializeWorker(context);
        }

        protected abstract void InitializeWorker(AnalysisContext context);

        protected bool ShouldSkipAnalysis(SemanticModelAnalysisContext context, NotificationOption2? notification)
            => ShouldSkipAnalysis(context.FilterTree, context.Options, notification);

        protected bool ShouldSkipAnalysis(SyntaxNodeAnalysisContext context, NotificationOption2? notification)
            => ShouldSkipAnalysis(context.Node.SyntaxTree, context.Options, notification);

        protected bool ShouldSkipAnalysis(SyntaxTreeAnalysisContext context, NotificationOption2? notification)
            => ShouldSkipAnalysis(context.Tree, context.Options, notification);

        protected bool ShouldSkipAnalysis(CodeBlockAnalysisContext context, NotificationOption2? notification)
            => ShouldSkipAnalysis(context.FilterTree, context.Options, notification);

        protected bool ShouldSkipAnalysis(OperationAnalysisContext context, NotificationOption2? notification)
            => ShouldSkipAnalysis(context.FilterTree, context.Options, notification);

        protected bool ShouldSkipAnalysis(OperationBlockAnalysisContext context, NotificationOption2? notification)
            => ShouldSkipAnalysis(context.FilterTree, context.Options, notification);

        protected bool ShouldSkipAnalysis(SyntaxTree tree, AnalyzerOptions analyzerOptions, NotificationOption2? notification)
            => ShouldSkipAnalysis(tree, analyzerOptions, notification, performDescriptorsCheck: true);

        protected bool ShouldSkipAnalysis(SyntaxTree tree, AnalyzerOptions analyzerOptions, ImmutableArray<NotificationOption2> notifications)
        {
            // We need to check if the analyzer's severity has been escalated either via 'option_name = option_value:severity'
            // setting or 'dotnet_diagnostic.RuleId.severity = severity'.
            // For the former, we check if any of the given notifications have been escalated via the ':severity' such
            // that analysis cannot be skipped. For the latter, we perform descriptor-based checks.
            // Descriptors check verifies if any of the diagnostic IDs reported by this analyzer
            // have been escalated to a severity that they must be executed.

            // PERF: Execute the descriptors check only once for the analyzer, not once per each notification option.
            var performDescriptorsCheck = true;

            // Check if any of the notifications are enabled, if so we need to execute analysis.
            foreach (var notification in notifications)
            {
                if (!ShouldSkipAnalysis(tree, analyzerOptions, notification, performDescriptorsCheck))
                    return false;

                if (performDescriptorsCheck)
                    performDescriptorsCheck = false;
            }

            return true;
        }

        private bool ShouldSkipAnalysis(SyntaxTree tree, AnalyzerOptions analyzerOptions, NotificationOption2? notification, bool performDescriptorsCheck)
        {
            // We need to check if the analyzer's severity has been escalated either via 'option_name = option_value:severity'
            // setting or 'dotnet_diagnostic.RuleId.severity = severity'.
            // For the former, we check if the given notification have been escalated via the ':severity' such
            // that analysis cannot be skipped. For the latter, we perform descriptor-based checks.
            // Descriptors check verifies if any of the diagnostic IDs reported by this analyzer
            // have been escalated to a severity that they must be executed.

            Debug.Assert(Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.CustomConfigurable));
            Debug.Assert(_minimumReportedSeverity != null);

            if (notification?.Severity == ReportDiagnostic.Suppress)
                return true;

            // If _minimumReportedSeverity is 'Hidden', then we are reporting diagnostics with all severities.
            if (_minimumReportedSeverity!.Value == DiagnosticSeverity.Hidden)
                return false;

            // If the severity is explicitly configured with `option_name = option_value:severity`,
            // we should skip analysis if the configured severity is lesser than the mininum reported severity.
            if (notification.HasValue && notification.Value.IsExplicitlySpecified)
            {
                return notification.Value.Severity.ToDiagnosticSeverity() < _minimumReportedSeverity.Value;
            }

            if (!performDescriptorsCheck)
                return true;

            // Otherwise, we check if any of the descriptors have been configured or bulk-configured
            // in editorconfig/globalconfig options to a severity that is greater than or equal to
            // the minimum reported severity.
            // If so, we should execute analysis. Otherwise, analysis should be skipped.

            var globalOptions = analyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
            var treeOptions = analyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);

            const string DotnetAnalyzerDiagnosticPrefix = "dotnet_analyzer_diagnostic";
            const string DotnetDiagnosticPrefix = "dotnet_diagnostic";
            const string CategoryPrefix = "category";
            const string SeveritySuffix = "severity";

            var allDiagnosticsBulkSeverityKey = $"{DotnetAnalyzerDiagnosticPrefix}.{SeveritySuffix}";
            var hasAllBulkSeverityConfiguration = treeOptions.TryGetValue(allDiagnosticsBulkSeverityKey, out var editorConfigBulkSeverity)
                || globalOptions.TryGetValue(allDiagnosticsBulkSeverityKey, out editorConfigBulkSeverity);

            foreach (var descriptor in SupportedDiagnostics)
            {
                if (descriptor.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable))
                    continue;

                var idConfigurationKey = $"{DotnetDiagnosticPrefix}.{descriptor.Id}.{SeveritySuffix}";
                var categoryConfigurationKey = $"{DotnetAnalyzerDiagnosticPrefix}.{CategoryPrefix}-{descriptor.Category}.{SeveritySuffix}";
                if (treeOptions.TryGetValue(idConfigurationKey, out var editorConfigSeverity)
                    || globalOptions.TryGetValue(idConfigurationKey, out editorConfigSeverity)
                    || treeOptions.TryGetValue(categoryConfigurationKey, out editorConfigSeverity)
                    || globalOptions.TryGetValue(categoryConfigurationKey, out editorConfigSeverity))
                {
                }
                else if (hasAllBulkSeverityConfiguration)
                {
                    editorConfigSeverity = editorConfigBulkSeverity;
                }
                else
                {
                    continue;
                }

                Debug.Assert(editorConfigSeverity != null);
                if (EditorConfigSeverityStrings.TryParse(editorConfigSeverity!, out var effectiveReportDiagnostic)
                    && effectiveReportDiagnostic.ToDiagnosticSeverity() is { } effectiveSeverity
                    && effectiveSeverity >= _minimumReportedSeverity.Value)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

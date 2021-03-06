﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Editor.Commands;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.AutomaticCompletion
{
    /// <summary>
    /// abstract line ender command handler
    /// </summary>
    internal abstract class AbstractAutomaticLineEnderCommandHandler :
        ICommandHandler<AutomaticLineEnderCommandArgs>
    {
        private readonly IWaitIndicator _waitIndicator;
        private readonly ITextUndoHistoryRegistry _undoRegistry;
        private readonly IEditorOperationsFactoryService _editorOperationsFactoryService;

        public AbstractAutomaticLineEnderCommandHandler(
            IWaitIndicator waitIndicator,
            ITextUndoHistoryRegistry undoRegistry,
            IEditorOperationsFactoryService editorOperationsFactoryService)
        {
            _waitIndicator = waitIndicator;
            _undoRegistry = undoRegistry;
            _editorOperationsFactoryService = editorOperationsFactoryService;
        }

        /// <summary>
        /// get ending string if there is one
        /// </summary>
        protected abstract string GetEndingString(Document document, int position, CancellationToken cancellationToken);

        /// <summary>
        /// do next action
        /// </summary>
        protected abstract void NextAction(IEditorOperations editorOperation, Action nextAction);

        /// <summary>
        /// format after inserting ending string
        /// </summary>
        protected abstract void FormatAndApply(Document document, int position, CancellationToken cancellationToken);

        public CommandState GetCommandState(AutomaticLineEnderCommandArgs args, Func<CommandState> nextHandler)
        {
            return CommandState.Available;
        }

        public void ExecuteCommand(AutomaticLineEnderCommandArgs args, Action nextHandler)
        {
            // get editor operation
            var operations = _editorOperationsFactoryService.GetEditorOperations(args.TextView);
            if (operations == null)
            {
                nextHandler();
                return;
            }

            var document = args.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                NextAction(operations, nextHandler);
                return;
            }

            // feature off
            if (!document.Project.Solution.Workspace.Options.GetOption(InternalFeatureOnOffOptions.AutomaticLineEnder))
            {
                NextAction(operations, nextHandler);
                return;
            }

            _waitIndicator.Wait(
                title: EditorFeaturesResources.AutomaticLineEnder,
                message: EditorFeaturesResources.AutomaticallyCompleting,
                allowCancel: false, action: w =>
                {
                    // caret is not on the subject buffer. nothing we can do
                    var position = args.TextView.GetCaretPoint(args.SubjectBuffer);
                    if (!position.HasValue)
                    {
                        NextAction(operations, nextHandler);
                        return;
                    }

                    // try to move the caret to the end of the line on which the caret is
                    var subjectLineWhereCaretIsOn = position.Value.GetContainingLine();
                    args.TextView.TryMoveCaretToAndEnsureVisible(subjectLineWhereCaretIsOn.End);

                    var insertionPoint = GetInsertionPoint(document, subjectLineWhereCaretIsOn, w.CancellationToken);
                    if (!insertionPoint.HasValue)
                    {
                        NextAction(operations, nextHandler);
                        return;
                    }

                    using (var transaction = args.TextView.CreateEditTransaction(EditorFeaturesResources.AutomaticLineEnder, _undoRegistry, _editorOperationsFactoryService))
                    {
                        // okay, now insert ending if we need to
                        var newDocument = InsertEndingIfRequired(document, insertionPoint.Value, position.Value, w.CancellationToken);

                        // format the document and apply the changes to the workspace
                        FormatAndApply(newDocument, insertionPoint.Value, w.CancellationToken);

                        // now, insert new line
                        NextAction(operations, nextHandler);

                        transaction.Complete();
                    }
                });
        }

        /// <summary>
        /// return insertion point for the ending string
        /// </summary>
        private int? GetInsertionPoint(Document document, ITextSnapshotLine line, CancellationToken cancellationToken)
        {
            var root = document.GetSyntaxRootAsync(cancellationToken).WaitAndGetResult(cancellationToken);
            var text = document.GetTextAsync(cancellationToken).WaitAndGetResult(cancellationToken);

            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();

            // find last token on the line
            var token = syntaxFacts.FindTokenOnLeftOfPosition(root, line.End);
            if (token.RawKind == 0)
            {
                return null;
            }

            // bug # 16770
            // don't do anything if token is multiline token such as verbatim string
            if (line.End < token.Span.End)
            {
                return null;
            }

            // if there is only whitespace, token doesn't need to be on same line
            if (string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(token.Span.End, line.End))))
            {
                return line.End;
            }

            // if token is on different line than caret but caret line is empty, we insert ending point at the end of the line
            if (text.Lines.IndexOf(token.Span.End) != text.Lines.IndexOf(line.End))
            {
                return string.IsNullOrWhiteSpace(line.GetText()) ? (int?)line.End : null;
            }

            return token.Span.End;
        }

        /// <summary>
        /// insert ending string if there is one to insert
        /// </summary>
        private Document InsertEndingIfRequired(Document document, int insertPosition, int caretPosition, CancellationToken cancellationToken)
        {
            var endingString = GetEndingString(document, caretPosition, cancellationToken);
            if (endingString == null)
            {
                return document;
            }

            // apply end string to workspace
            return document.InsertText(insertPosition, endingString, cancellationToken);
        }
    }
}

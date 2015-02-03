﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.LanguageServices.Implementation.Venus;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Diagnostics
{
    [ExportWorkspaceService(typeof(IWorkspaceVenusSpanMappingService), ServiceLayer.Default), Shared]
    internal partial class VisualStudioVenusSpanMappingService : IWorkspaceVenusSpanMappingService
    {
        private readonly VisualStudioWorkspaceImpl _workspace;

        [ImportingConstructor]
        public VisualStudioVenusSpanMappingService(VisualStudioWorkspaceImpl workspace)
        {
            _workspace = workspace;
        }

        public void GetAdjustedDiagnosticSpan(
            DocumentId documentId, Location location,
            out TextSpan sourceSpan, out FileLinePositionSpan originalLineInfo, out FileLinePositionSpan mappedLineInfo)
        {
            sourceSpan = location.SourceSpan;
            originalLineInfo = location.GetLineSpan();
            mappedLineInfo = location.GetMappedLineSpan();

            // Update the original source span, if required.
            LinePositionSpan originalSpan;
            LinePositionSpan mappedSpan;
            if (!TryAdjustSpanIfNeededForVenus(documentId, originalLineInfo, mappedLineInfo, out originalSpan, out mappedSpan))
            {
                return;
            }

            if (originalSpan.Start != originalLineInfo.StartLinePosition || originalSpan.End != originalLineInfo.EndLinePosition)
            {
                originalLineInfo = new FileLinePositionSpan(originalLineInfo.Path, originalSpan.Start, originalSpan.End);

                var textLines = location.SourceTree.GetText().Lines;
                var startPos = textLines.GetPosition(originalSpan.Start);
                var endPos = textLines.GetPosition(originalSpan.End);
                sourceSpan = new TextSpan(startPos, endPos - startPos);
            }

            if (mappedSpan.Start != mappedLineInfo.StartLinePosition || mappedSpan.End != mappedLineInfo.EndLinePosition)
            {
                mappedLineInfo = new FileLinePositionSpan(mappedLineInfo.Path, mappedSpan.Start, mappedSpan.End);
            }
        }

        private bool TryAdjustSpanIfNeededForVenus(
            DocumentId documentId, FileLinePositionSpan originalLineInfo, FileLinePositionSpan mappedLineInfo, out LinePositionSpan originalSpan, out LinePositionSpan mappedSpan)
        {
            var startChanged = true;
            MappedSpan startLineColumn;
            if (!TryAdjustSpanIfNeededForVenus(_workspace, documentId, originalLineInfo.StartLinePosition.Line, originalLineInfo.StartLinePosition.Character, out startLineColumn))
            {
                startChanged = false;
                startLineColumn = new MappedSpan(originalLineInfo.StartLinePosition.Line, originalLineInfo.StartLinePosition.Character, mappedLineInfo.StartLinePosition.Line, mappedLineInfo.StartLinePosition.Character);
            }

            var endChanged = true;
            MappedSpan endLineColumn;
            if (!TryAdjustSpanIfNeededForVenus(_workspace, documentId, originalLineInfo.EndLinePosition.Line, originalLineInfo.EndLinePosition.Character, out endLineColumn))
            {
                endChanged = false;
                endLineColumn = new MappedSpan(originalLineInfo.EndLinePosition.Line, originalLineInfo.EndLinePosition.Character, mappedLineInfo.EndLinePosition.Line, mappedLineInfo.EndLinePosition.Character);
            }

            originalSpan = new LinePositionSpan(startLineColumn.OriginalLinePosition, Max(startLineColumn.OriginalLinePosition, endLineColumn.OriginalLinePosition));
            mappedSpan = new LinePositionSpan(startLineColumn.MappedLinePosition, Max(startLineColumn.MappedLinePosition, endLineColumn.MappedLinePosition));
            return startChanged || endChanged;
        }

        private static LinePosition Max(LinePosition position1, LinePosition position2)
        {
            return position1 > position2 ? position1 : position2;
        }

        public static LinePosition GetAdjustedLineColumn(Workspace workspace, DocumentId documentId, int originalLine, int originalColumn, int mappedLine, int mappedColumn)
        {
            var vsWorkspace = workspace as VisualStudioWorkspaceImpl;
            if (vsWorkspace == null)
            {
                return new LinePosition(mappedLine, mappedColumn);
            }

            MappedSpan span;
            if (TryAdjustSpanIfNeededForVenus(vsWorkspace, documentId, originalLine, originalColumn, out span))
            {
                return span.MappedLinePosition;
            }

            return new LinePosition(mappedLine, mappedColumn);
        }

        private static bool TryAdjustSpanIfNeededForVenus(VisualStudioWorkspaceImpl workspace, DocumentId documentId, int originalLine, int originalColumn, out MappedSpan mappedSpan)
        {
            mappedSpan = default(MappedSpan);

            if (documentId == null)
            {
                return false;
            }

            var containedDocument = workspace.GetHostDocument(documentId) as ContainedDocument;
            if (containedDocument == null)
            {
                return false;
            }

            var originalSpanOnSecondaryBuffer = new TextManager.Interop.TextSpan()
            {
                iStartLine = originalLine,
                iStartIndex = originalColumn,
                iEndLine = originalLine,
                iEndIndex = originalColumn
            };

            var containedLanguage = containedDocument.ContainedLanguage;
            var bufferCoordinator = containedLanguage.BufferCoordinator;
            var containedLanguageHost = containedLanguage.ContainedLanguageHost;

            var spansOnPrimaryBuffer = new TextManager.Interop.TextSpan[1];
            if (VSConstants.S_OK == bufferCoordinator.MapSecondaryToPrimarySpan(originalSpanOnSecondaryBuffer, spansOnPrimaryBuffer))
            {
                // easy case, we can map span in subject buffer to surface buffer. no need to adjust any span
                mappedSpan = new MappedSpan(originalLine, originalColumn, spansOnPrimaryBuffer[0].iStartLine, spansOnPrimaryBuffer[0].iStartIndex);
                return true;
            }

            // we can't directly map span in subject buffer to surface buffer. see whether there is any visible span we can use from the subject buffer span
            if (VSConstants.S_OK != containedLanguageHost.GetNearestVisibleToken(originalSpanOnSecondaryBuffer, spansOnPrimaryBuffer))
            {
                // no visible span we can use.
                return false;
            }

            // We need to map both the original and mapped location into visible code so that features such as error list, squiggle, etc. points to user visible area
            // We have the mapped location in the primary buffer.
            var nearestVisibleSpanOnPrimaryBuffer = new TextManager.Interop.TextSpan()
            {
                iStartLine = spansOnPrimaryBuffer[0].iStartLine,
                iStartIndex = spansOnPrimaryBuffer[0].iStartIndex,
                iEndLine = spansOnPrimaryBuffer[0].iStartLine,
                iEndIndex = spansOnPrimaryBuffer[0].iStartIndex
            };

            // Map this location back to the secondary span to re-adjust the original location to be in user-code in secondary buffer.
            var spansOnSecondaryBuffer = new TextManager.Interop.TextSpan[1];
            if (VSConstants.S_OK != bufferCoordinator.MapPrimaryToSecondarySpan(nearestVisibleSpanOnPrimaryBuffer, spansOnSecondaryBuffer))
            {
                // we can't adjust original position but we can adjust mapped one
                mappedSpan = new MappedSpan(originalLine, originalColumn, nearestVisibleSpanOnPrimaryBuffer.iStartLine, nearestVisibleSpanOnPrimaryBuffer.iStartIndex);
                return true;
            }

            var nearestVisibleSpanOnSecondaryBuffer = spansOnSecondaryBuffer[0];
            var originalLocationMovedAboveInFile = IsOriginalLocationMovedAboveInFile(originalLine, originalColumn, nearestVisibleSpanOnSecondaryBuffer.iStartLine, nearestVisibleSpanOnSecondaryBuffer.iStartIndex);

            if (!originalLocationMovedAboveInFile)
            {
                mappedSpan = new MappedSpan(nearestVisibleSpanOnSecondaryBuffer.iStartLine, nearestVisibleSpanOnSecondaryBuffer.iStartIndex, nearestVisibleSpanOnPrimaryBuffer.iStartLine, nearestVisibleSpanOnPrimaryBuffer.iStartIndex);
                return true;
            }

            LinePosition adjustedPosition;
            if (TryFixUpNearestVisibleSpan(containedLanguageHost, bufferCoordinator, nearestVisibleSpanOnSecondaryBuffer.iStartLine, nearestVisibleSpanOnSecondaryBuffer.iStartIndex, out adjustedPosition))
            {
                // span has changed yet again, re-calculate span
                return TryAdjustSpanIfNeededForVenus(workspace, documentId, adjustedPosition.Line, adjustedPosition.Character, out mappedSpan);
            }

            mappedSpan = new MappedSpan(nearestVisibleSpanOnSecondaryBuffer.iStartLine, nearestVisibleSpanOnSecondaryBuffer.iStartIndex, nearestVisibleSpanOnPrimaryBuffer.iStartLine, nearestVisibleSpanOnPrimaryBuffer.iStartIndex);
            return true;
        }

        private static bool TryFixUpNearestVisibleSpan(
            TextManager.Interop.IVsContainedLanguageHost containedLanguageHost, TextManager.Interop.IVsTextBufferCoordinator bufferCoordinator,
            int originalLine, int originalColumn, out LinePosition adjustedPosition)
        {
            // GetNearestVisibleToken gives us the position right at the end of visible span.
            // Move the position one position to the left so that squiggle can show up on last token.
            if (originalColumn > 1)
            {
                adjustedPosition = new LinePosition(originalLine, originalColumn - 1);
                return true;
            }

            if (originalLine > 1)
            {
                TextManager.Interop.IVsTextLines secondaryBuffer;
                int length;
                if (VSConstants.S_OK == bufferCoordinator.GetSecondaryBuffer(out secondaryBuffer) &&
                    VSConstants.S_OK == secondaryBuffer.GetLengthOfLine(originalLine - 1, out length))
                {
                    adjustedPosition = new LinePosition(originalLine - 1, length);
                    return true;
                }
            }

            adjustedPosition = LinePosition.Zero;
            return false;
        }

        private static bool IsOriginalLocationMovedAboveInFile(int originalLine, int originalColumn, int movedLine, int movedColumn)
        {
            if (movedLine < originalLine)
            {
                return true;
            }

            if (movedLine == originalLine && movedColumn < originalColumn)
            {
                return true;
            }

            return false;
        }

        private struct MappedSpan
        {
            private readonly int _originalLine;
            private readonly int _originalColumn;
            private readonly int _mappedLine;
            private readonly int _mappedColumn;

            public MappedSpan(int originalLine, int originalColumn, int mappedLine, int mappedColumn)
            {
                _originalLine = originalLine;
                _originalColumn = originalColumn;
                _mappedLine = mappedLine;
                _mappedColumn = mappedColumn;
            }

            public LinePosition OriginalLinePosition
            {
                get { return new LinePosition(_originalLine, _originalColumn); }
            }

            public LinePosition MappedLinePosition
            {
                get { return new LinePosition(_mappedLine, _mappedColumn); }
            }
        }
    }
}
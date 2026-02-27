using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Win32;
using TxtEditorSkeleton.ViewModels;

namespace TxtEditorSkeleton;

public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        ViewModel?.AttachEditorService(new RichTextBoxEditorService(EditorBox));
        Editor_OnSelectionChanged(EditorBox, new RoutedEventArgs());
    }

    private void Editor_OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox editor || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var selection = editor.Selection;

        var isBold = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight fontWeight
            && fontWeight == FontWeights.Bold;

        var isItalic = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle fontStyle
            && fontStyle == FontStyles.Italic;

        var decorationsValue = selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var decorations = decorationsValue as TextDecorationCollection;

        var isUnderline = decorations?.Any(x => x.Location == TextDecorationLocation.Underline) == true;
        var isStrike = decorations?.Any(x => x.Location == TextDecorationLocation.Strikethrough) == true;

        var paragraphAlignment = selection.Start.Paragraph?.TextAlignment.ToString() ?? TextAlignment.Left.ToString();
        var hasSelection = !selection.IsEmpty;

        viewModel.UpdateEditorDebugState(isBold, isItalic, isUnderline, isStrike, paragraphAlignment, hasSelection);
    }

    private void Editor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Tab)
        {
            var decrease = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            viewModel.ApplyParagraphIndent(decrease);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            if (viewModel.ApplyAtomicDelete(e.Key))
            {
                e.Handled = true;
            }
        }
    }

    private sealed class RichTextBoxEditorService : ITextEditorService
    {
        private const double IndentStep = 24;
        private const string FootnotesHeaderText = "Сноски";
        private const string FootnoteNoteUriPrefix = "footnote-note://";
        private const string FootnoteRefUriPrefix = "footnote-ref://";
        private const string DividerParagraphTag = "DIVIDER";
        private const string DividerStyleKey = "DividerStyle";

        private readonly RichTextBox _editor;
        private bool _hasUnsavedChanges;
        private bool _isApplyingTypographyRules;
        private string? _currentFilePath;

        public RichTextBoxEditorService(RichTextBox editor)
        {
            _editor = editor;
            _editor.SelectionChanged += (_, _) => FormattingStateChanged?.Invoke();
            _editor.TextChanged += (_, _) => OnEditorTextChanged();
            _editor.AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(OnHyperlinkClick));

            _hasUnsavedChanges = false;
        }

        public event Action? FormattingStateChanged;

        private void OnEditorTextChanged()
        {
            if (_isApplyingTypographyRules)
            {
                return;
            }

            _isApplyingTypographyRules = true;
            try
            {
                ApplyTypographyRulesAroundCaret();
            }
            finally
            {
                _isApplyingTypographyRules = false;
            }

            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
        }

        public bool CanEdit => _editor.IsEnabled && !_editor.IsReadOnly;

        public bool CanUndo => _editor.CanUndo;

        public bool CanRedo => _editor.CanRedo;

        public bool HasUnsavedChanges => _hasUnsavedChanges;

        public string? CurrentFilePath => _currentFilePath;

        public bool IsBoldActive =>
            _editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight fontWeight
            && fontWeight == FontWeights.Bold;

        public bool IsItalicActive =>
            _editor.Selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle fontStyle
            && fontStyle == FontStyles.Italic;

        public bool IsUnderlineActive
        {
            get
            {
                var value = _editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
                var decorations = value as TextDecorationCollection;
                return decorations?.Any(x => x.Location == TextDecorationLocation.Underline) == true;
            }
        }

        public bool IsStrikethroughActive
        {
            get
            {
                var value = _editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
                var decorations = value as TextDecorationCollection;
                return decorations?.Any(x => x.Location == TextDecorationLocation.Strikethrough) == true;
            }
        }

        public TextAlignment CurrentParagraphAlignment =>
            _editor.Selection.Start.Paragraph?.TextAlignment ?? TextAlignment.Left;


        public void IncreaseParagraphIndent()
        {
            ApplyParagraphIndent(change: IndentStep);
        }

        public void DecreaseParagraphIndent()
        {
            ApplyParagraphIndent(change: -IndentStep);
        }

        private void ApplyParagraphIndent(double change)
        {
            if (!CanEdit)
            {
                return;
            }

            var selection = _editor.Selection;
            var start = selection.Start;
            var end = selection.End;

            var paragraphs = EnumerateSelectedParagraphs(selection).ToList();
            if (paragraphs.Count == 0)
            {
                return;
            }

            foreach (var paragraph in paragraphs)
            {
                paragraph.TextIndent = Math.Max(0, paragraph.TextIndent + change);
            }

            _editor.Selection.Select(start, end);
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void InsertDivider()
        {
            if (!CanEdit)
            {
                return;
            }

            var caretParagraph = _editor.CaretPosition.Paragraph;
            var divider = new Paragraph(new Run("────────"))
            {
                Tag = DividerParagraphTag
            };

            if (_editor.Document.Resources[DividerStyleKey] is Style style)
            {
                divider.Style = style;
            }

            if (caretParagraph is null)
            {
                _editor.Document.Blocks.Add(divider);
            }
            else
            {
                _editor.Document.Blocks.InsertAfter(caretParagraph, divider);
            }

            var nextParagraph = new Paragraph();
            _editor.Document.Blocks.InsertAfter(divider, nextParagraph);
            _editor.CaretPosition = nextParagraph.ContentStart;
            _editor.Selection.Select(_editor.CaretPosition, _editor.CaretPosition);

            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public bool HandleAtomicDelete(Key key)
        {
            var selection = _editor.Selection;
            if (!selection.IsEmpty)
            {
                return false;
            }

            var caret = _editor.CaretPosition;
            var currentParagraph = caret.Paragraph;

            if (IsDividerParagraph(currentParagraph))
            {
                RemoveDividerParagraph(currentParagraph!);
                return true;
            }

            if (key == Key.Back && currentParagraph is not null)
            {
                var start = currentParagraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward) ?? currentParagraph.ContentStart;
                if (caret.CompareTo(start) == 0 && currentParagraph.PreviousBlock is Paragraph previous && IsDividerParagraph(previous))
                {
                    RemoveDividerParagraph(previous);
                    return true;
                }
            }

            if (key == Key.Delete && currentParagraph is not null)
            {
                var end = currentParagraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) ?? currentParagraph.ContentEnd;
                if (caret.CompareTo(end) == 0 && currentParagraph.NextBlock is Paragraph next && IsDividerParagraph(next))
                {
                    RemoveDividerParagraph(next);
                    return true;
                }
            }

            return false;
        }

        private bool IsDividerParagraph(Paragraph? paragraph)
        {
            return paragraph?.Tag as string == DividerParagraphTag;
        }

        private void RemoveDividerParagraph(Paragraph dividerParagraph)
        {
            TextPointer targetPosition;
            if (dividerParagraph.NextBlock is Paragraph next)
            {
                targetPosition = next.ContentStart.GetInsertionPosition(LogicalDirection.Forward) ?? next.ContentStart;
            }
            else if (dividerParagraph.PreviousBlock is Paragraph prev)
            {
                targetPosition = prev.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) ?? prev.ContentEnd;
            }
            else
            {
                targetPosition = _editor.Document.ContentEnd;
            }

            _editor.Document.Blocks.Remove(dividerParagraph);
            _editor.Selection.Select(targetPosition, targetPosition);
            _editor.Focus();
            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
        }

        public void InsertFootnote()
        {
            if (!CanEdit)
            {
                return;
            }

            var id = GetNextFootnoteId();
            var insertionPosition = _editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            if (insertionPosition is null)
            {
                return;
            }

            var marker = new Hyperlink(insertionPosition, insertionPosition)
            {
                NavigateUri = new Uri($"{FootnoteNoteUriPrefix}{id}"),
                BaselineAlignment = BaselineAlignment.Superscript,
                FontSize = Math.Max(8, _editor.FontSize - 2),
                Tag = id
            };
            marker.Inlines.Add(new Run($"[{id}]"));

            RenumberFootnotes();
            var normalizedId = TryExtractId(marker.NavigateUri?.ToString(), FootnoteNoteUriPrefix) ?? id;
            var footnoteParagraph = FindFootnoteParagraphById(normalizedId) ?? EnsureFootnoteParagraph(normalizedId);
            var contentPointer = footnoteParagraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
                ?? footnoteParagraph.ContentEnd;
            _editor.Selection.Select(contentPointer, contentPointer);
            _editor.Focus();

            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
        }
        private Paragraph EnsureFootnoteParagraph(int id)
        {
            EnsureFootnotesSectionExists();
            var sectionStart = FindFootnotesHeaderParagraph();

            var paragraph = new Paragraph();
            var backLink = new Hyperlink(new Run($"[{id}] "))
            {
                NavigateUri = new Uri($"{FootnoteRefUriPrefix}{id}"),
                Tag = id
            };
            paragraph.Inlines.Add(backLink);
            paragraph.Inlines.Add(new Run("текст сноски"));

            if (sectionStart is null)
            {
                _editor.Document.Blocks.Add(paragraph);
                return paragraph;
            }

            Block insertAfter = sectionStart;
            var current = sectionStart.NextBlock;
            while (current is Paragraph p && IsFootnoteParagraph(p))
            {
                insertAfter = current;
                current = current.NextBlock;
            }

            _editor.Document.Blocks.InsertAfter(insertAfter, paragraph);
            return paragraph;
        }

        private bool IsFootnoteParagraph(Paragraph paragraph)
        {
            return paragraph.Inlines.FirstInline is Hyperlink hyperlink
                && hyperlink.NavigateUri?.ToString().StartsWith(FootnoteRefUriPrefix) == true;
        }

        private void EnsureFootnotesSectionExists()
        {
            if (FindFootnotesHeaderParagraph() is not null)
            {
                return;
            }

            var heading = new Paragraph(new Run(FootnotesHeaderText))
            {
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 6)
            };
            _editor.Document.Blocks.Add(heading);
        }

        private Paragraph? FindFootnotesHeaderParagraph()
        {
            return _editor.Document.Blocks
                .OfType<Paragraph>()
                .FirstOrDefault(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim() == FootnotesHeaderText);
        }

        private int GetNextFootnoteId()
        {
            var maxId = 0;
            foreach (var hyperlink in EnumerateHyperlinks(_editor.Document))
            {
                if (hyperlink.NavigateUri is null)
                {
                    continue;
                }

                var uri = hyperlink.NavigateUri.ToString();
                if (!uri.StartsWith(FootnoteNoteUriPrefix))
                {
                    continue;
                }

                if (int.TryParse(uri[FootnoteNoteUriPrefix.Length..], out var id))
                {
                    maxId = Math.Max(maxId, id);
                }
            }

            return maxId + 1;
        }

        private static IEnumerable<Hyperlink> EnumerateHyperlinks(FlowDocument document)
        {
            foreach (var paragraph in document.Blocks.OfType<Paragraph>())
            {
                foreach (var hyperlink in paragraph.Inlines.OfType<Hyperlink>())
                {
                    yield return hyperlink;
                }
            }
        }

        private void OnHyperlinkClick(object sender, RoutedEventArgs e)
        {
            var hyperlink = e.OriginalSource switch
            {
                Hyperlink direct => direct,
                Run run => run.Parent as Hyperlink,
                _ => null
            };

            if (hyperlink?.NavigateUri is null)
            {
                return;
            }

            var uri = hyperlink.NavigateUri.ToString();
            if (uri.StartsWith(FootnoteNoteUriPrefix))
            {
                NavigateToHyperlink(FootnoteRefUriPrefix + uri[FootnoteNoteUriPrefix.Length..]);
                e.Handled = true;
                return;
            }

            if (uri.StartsWith(FootnoteRefUriPrefix))
            {
                NavigateToHyperlink(FootnoteNoteUriPrefix + uri[FootnoteRefUriPrefix.Length..]);
                e.Handled = true;
            }
        }

        private void NavigateToHyperlink(string targetUri)
        {
            var target = EnumerateHyperlinks(_editor.Document)
                .FirstOrDefault(h => h.NavigateUri?.ToString() == targetUri);

            if (target is null)
            {
                return;
            }

            _editor.Selection.Select(target.ContentStart, target.ContentEnd);
            _editor.Focus();
        }

        public void RenumberFootnotes()
        {
            var header = FindFootnotesHeaderParagraph();

            var markerLinks = EnumerateHyperlinks(_editor.Document)
                .Where(h => h.NavigateUri?.ToString().StartsWith(FootnoteNoteUriPrefix) == true)
                .ToList();

            var footnoteEntries = GetFootnoteEntryParagraphs(header).ToList();
            var entryByOldId = footnoteEntries
                .Select(p => new { Paragraph = p, Id = GetFootnoteParagraphId(p) })
                .Where(x => x.Id is not null)
                .GroupBy(x => x.Id!.Value)
                .ToDictionary(g => g.Key, g => new Queue<Paragraph>(g.Select(x => x.Paragraph)));

            var reorderedEntries = new List<Paragraph>();
            var nextId = 1;

            foreach (var marker in markerLinks)
            {
                var oldId = TryExtractId(marker.NavigateUri?.ToString(), FootnoteNoteUriPrefix);
                UpdateMarkerLink(marker, nextId);

                Paragraph? entry = null;
                if (oldId is not null && entryByOldId.TryGetValue(oldId.Value, out var queue) && queue.Count > 0)
                {
                    entry = queue.Dequeue();
                }

                if (entry is null)
                {
                    entry = CreateFootnoteParagraph(nextId);
                }
                else
                {
                    UpdateFootnoteParagraphLink(entry, nextId);
                }

                reorderedEntries.Add(entry);
                nextId++;
            }

            foreach (var paragraph in footnoteEntries)
            {
                _editor.Document.Blocks.Remove(paragraph);
            }

            if (reorderedEntries.Count == 0)
            {
                if (header is not null)
                {
                    _editor.Document.Blocks.Remove(header);
                }

                _hasUnsavedChanges = true;
                return;
            }

            EnsureFootnotesSectionExists();
            header = FindFootnotesHeaderParagraph();
            if (header is null)
            {
                return;
            }

            Block insertAfter = header;
            foreach (var paragraph in reorderedEntries)
            {
                _editor.Document.Blocks.InsertAfter(insertAfter, paragraph);
                insertAfter = paragraph;
            }

            _hasUnsavedChanges = true;
        }

        private void ApplyTypographyRulesAroundCaret()
        {
            var paragraph = _editor.CaretPosition.Paragraph;
            if (paragraph is null || IsCodeParagraph(paragraph))
            {
                return;
            }

            var start = _editor.Selection.Start;
            var end = _editor.Selection.End;

            var changed = false;
            foreach (var run in EnumerateRuns(paragraph.Inlines))
            {
                if (IsCodeRun(run))
                {
                    continue;
                }

                var processed = ApplyTypographyRules(run.Text);
                if (!string.Equals(processed, run.Text, StringComparison.Ordinal))
                {
                    run.Text = processed;
                    changed = true;
                }
            }

            if (changed)
            {
                _editor.Selection.Select(start, end);
            }
        }

        private static IEnumerable<Run> EnumerateRuns(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        yield return run;
                        break;
                    case Span span:
                        foreach (var nested in EnumerateRuns(span.Inlines))
                        {
                            yield return nested;
                        }

                        break;
                }
            }
        }

        private bool IsCodeParagraph(Paragraph paragraph)
        {
            if (paragraph.Tag as string == "CODE")
            {
                return true;
            }

            return IsCodeStyle(paragraph.Style);
        }

        private bool IsCodeRun(Run run)
        {
            if (run.Tag as string == "CODE")
            {
                return true;
            }

            if (run.Parent is Paragraph paragraph && IsCodeParagraph(paragraph))
            {
                return true;
            }

            return IsCodeStyle(run.Style);
        }

        private bool IsCodeStyle(Style? style)
        {
            if (style is null)
            {
                return false;
            }

            return _editor.Document.Resources.Contains("Code")
                && _editor.Document.Resources["Code"] is Style codeStyle
                && ReferenceEquals(style, codeStyle);
        }

        private static string ApplyTypographyRules(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            // Правило 1: заменяем дефис на тире только в текстовых конструкциях, но не в числовых диапазонах (10-12, 10 - 12).
            var dashNormalized = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\d)\s-\s(?!\d)", " — ");
            dashNormalized = System.Text.RegularExpressions.Regex.Replace(dashNormalized, @"(?<!\d)--(?!\d)", "—");

            // Правило 2: вложенные кавычки по схеме «уровень 1 “уровень 2” уровень 1».
            return NormalizeNestedQuotes(dashNormalized);
        }

        private static string NormalizeNestedQuotes(string input)
        {
            var result = new System.Text.StringBuilder(input.Length);
            var level = 0;

            for (var i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (ch != '"')
                {
                    result.Append(ch);
                    continue;
                }

                var prev = i > 0 ? input[i - 1] : '\0';
                var next = i + 1 < input.Length ? input[i + 1] : '\0';
                var isOpening = i == 0 || char.IsWhiteSpace(prev) || "([{<«—".Contains(prev);
                var isClosing = i == input.Length - 1 || char.IsWhiteSpace(next) || ")]}>.,;:!?»".Contains(next);

                if (isOpening && !isClosing)
                {
                    level++;
                    result.Append(level <= 1 ? '«' : '“');
                    continue;
                }

                if (level > 0)
                {
                    result.Append(level <= 1 ? '»' : '”');
                    level--;
                }
                else
                {
                    result.Append('«');
                    level = 1;
                }
            }

            return result.ToString();
        }

        private static int? TryExtractId(string? uri, string prefix)
        {
            if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(prefix))
            {
                return null;
            }

            return int.TryParse(uri[prefix.Length..], out var id) ? id : null;
        }

        private static void UpdateMarkerLink(Hyperlink marker, int id)
        {
            marker.NavigateUri = new Uri($"{FootnoteNoteUriPrefix}{id}");
            marker.Tag = id;
            marker.Inlines.Clear();
            marker.Inlines.Add(new Run($"[{id}]"));
        }

        private static Paragraph CreateFootnoteParagraph(int id)
        {
            var paragraph = new Paragraph();
            var backLink = new Hyperlink(new Run($"[{id}] "))
            {
                NavigateUri = new Uri($"{FootnoteRefUriPrefix}{id}"),
                Tag = id
            };
            paragraph.Inlines.Add(backLink);
            paragraph.Inlines.Add(new Run("текст сноски"));
            return paragraph;
        }

        private static void UpdateFootnoteParagraphLink(Paragraph paragraph, int id)
        {
            if (paragraph.Inlines.FirstInline is Hyperlink hyperlink)
            {
                hyperlink.NavigateUri = new Uri($"{FootnoteRefUriPrefix}{id}");
                hyperlink.Tag = id;
                hyperlink.Inlines.Clear();
                hyperlink.Inlines.Add(new Run($"[{id}] "));
                return;
            }

            paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, new Hyperlink(new Run($"[{id}] "))
            {
                NavigateUri = new Uri($"{FootnoteRefUriPrefix}{id}"),
                Tag = id
            });
        }

        private Paragraph? FindFootnoteParagraphById(int id)
        {
            var header = FindFootnotesHeaderParagraph();
            return GetFootnoteEntryParagraphs(header)
                .FirstOrDefault(p => GetFootnoteParagraphId(p) == id);
        }

        private IEnumerable<Paragraph> GetFootnoteEntryParagraphs(Paragraph? header)
        {
            if (header is null)
            {
                yield break;
            }

            var current = header.NextBlock;
            while (current is Paragraph paragraph && IsFootnoteParagraph(paragraph))
            {
                yield return paragraph;
                current = current.NextBlock;
            }
        }

        private int? GetFootnoteParagraphId(Paragraph paragraph)
        {
            if (paragraph.Inlines.FirstInline is not Hyperlink hyperlink)
            {
                return null;
            }

            return TryExtractId(hyperlink.NavigateUri?.ToString(), FootnoteRefUriPrefix);
        }

        public bool SaveDocument()
        {
            if (!CanEdit)
            {
                return false;
            }

            RenumberFootnotes();

            var dialog = new SaveFileDialog
            {
                Title = "Сохранить документ",
                Filter = "RTF (*.rtf)|*.rtf|XAML Package (*.xamlpkg)|*.xamlpkg",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = string.IsNullOrWhiteSpace(_currentFilePath) ? "document" : Path.GetFileName(_currentFilePath)
            };

            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            var dataFormat = dialog.FilterIndex == 2 ? DataFormats.XamlPackage : DataFormats.Rtf;
            var textRange = new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd);

            using (var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                textRange.Save(stream, dataFormat);
            }

            _currentFilePath = dialog.FileName;
            _hasUnsavedChanges = false;
            FormattingStateChanged?.Invoke();
            return true;
        }

        public void Undo()
        {
            if (!CanEdit || !CanUndo)
            {
                return;
            }

            _editor.Undo();
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void Redo()
        {
            if (!CanEdit || !CanRedo)
            {
                return;
            }

            _editor.Redo();
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void ToggleBold()
        {
            if (!CanEdit)
            {
                return;
            }

            EditingCommands.ToggleBold.Execute(null, _editor);
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void ToggleItalic()
        {
            if (!CanEdit)
            {
                return;
            }

            EditingCommands.ToggleItalic.Execute(null, _editor);
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void ToggleUnderline()
        {
            if (!CanEdit)
            {
                return;
            }

            var value = _editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            var currentDecorations = value as TextDecorationCollection ?? new TextDecorationCollection();
            var hasUnderline = currentDecorations.Any(x => x.Location == TextDecorationLocation.Underline);

            var nextDecorations = new TextDecorationCollection();
            foreach (var decoration in currentDecorations.Where(x => x.Location != TextDecorationLocation.Underline))
            {
                nextDecorations.Add(decoration.Clone());
            }

            if (!hasUnderline)
            {
                nextDecorations.Add(TextDecorations.Underline[0].Clone());
            }

            _editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, nextDecorations);
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void ToggleStrikethrough()
        {
            if (!CanEdit)
            {
                return;
            }

            var value = _editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            var currentDecorations = value as TextDecorationCollection ?? new TextDecorationCollection();
            var hasStrikethrough = currentDecorations.Any(x => x.Location == TextDecorationLocation.Strikethrough);

            var nextDecorations = new TextDecorationCollection();
            foreach (var decoration in currentDecorations.Where(x => x.Location != TextDecorationLocation.Strikethrough))
            {
                nextDecorations.Add(decoration.Clone());
            }

            if (!hasStrikethrough)
            {
                nextDecorations.Add(TextDecorations.Strikethrough[0].Clone());
            }

            _editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, nextDecorations);
            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        public void SetParagraphAlignment(TextAlignment alignment)
        {
            if (!CanEdit)
            {
                return;
            }

            var paragraphs = EnumerateSelectedParagraphs(_editor.Selection).ToList();
            if (paragraphs.Count == 0)
            {
                return;
            }

            foreach (var paragraph in paragraphs)
            {
                paragraph.TextAlignment = alignment;
            }

            FormattingStateChanged?.Invoke();
            _editor.Focus();
        }

        private static IEnumerable<Paragraph> EnumerateSelectedParagraphs(TextSelection selection)
        {
            var startParagraph = selection.Start.Paragraph;
            if (startParagraph is null)
            {
                yield break;
            }

            var endParagraph = selection.End.Paragraph ?? startParagraph;

            Block? currentBlock = startParagraph;
            while (currentBlock is not null)
            {
                if (currentBlock is Paragraph paragraph)
                {
                    yield return paragraph;
                }

                if (ReferenceEquals(currentBlock, endParagraph))
                {
                    yield break;
                }

                currentBlock = currentBlock.NextBlock;
            }
        }
    }
}

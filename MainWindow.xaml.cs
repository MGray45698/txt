using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using TxtEditorSkeleton.ViewModels;

namespace TxtEditorSkeleton;

public partial class MainWindow : Window
{
    private const int AutoSaveIntervalSeconds = 15;
    private readonly string _autoSaveFilePath = Path.Combine(Path.GetTempPath(), "TxtEditorSkeleton", "autosave.xamlpkg");
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly EditorService _editorService;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel();
        _editorService = new EditorService(EditorBox);
        ViewModel?.AttachEditorService(_editorService);

        _autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AutoSaveIntervalSeconds)
        };
        _autoSaveTimer.Tick += (_, _) => PerformAutoSave();
        _autoSaveTimer.Start();

        Editor_OnSelectionChanged(EditorBox, new RoutedEventArgs());
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        TryRestoreAutoSavedDocument();
    }

    private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ViewModel?.HasUnsavedChanges == true)
        {
            var result = MessageBox.Show(
                "Есть несохранённые изменения. Сохранить перед закрытием?",
                "Подтверждение закрытия",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                if (ViewModel.SaveCommand.CanExecute(null))
                {
                    ViewModel.SaveCommand.Execute(null);
                }

                if (ViewModel.HasUnsavedChanges)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        _autoSaveTimer.Stop();
        TryDeleteAutoSaveFile();
    }

    private void PerformAutoSave()
    {
        if (ViewModel?.HasUnsavedChanges != true)
        {
            TryDeleteAutoSaveFile();
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_autoSaveFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _editorService.TryWriteAutoSaveSnapshot(_autoSaveFilePath);
        }
        catch
        {
            // Ignore autosave failures; editing should continue even when temp write fails.
        }
    }

    private void TryRestoreAutoSavedDocument()
    {
        if (!File.Exists(_autoSaveFilePath))
        {
            return;
        }

        var result = MessageBox.Show(
            "Найден файл автосохранения. Восстановить документ?",
            "Восстановление",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (_editorService.TryRestoreAutoSavedDocument(_autoSaveFilePath, out var error))
        {
            Editor_OnSelectionChanged(EditorBox, new RoutedEventArgs());
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(
                $"Не удалось восстановить автосохранение: {error}",
                "Восстановление",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void TryDeleteAutoSaveFile()
    {
        try
        {
            if (File.Exists(_autoSaveFilePath))
            {
                File.Delete(_autoSaveFilePath);
            }
        }
        catch
        {
            // Swallow cleanup issues for temp files.
        }
    }

    private void Editor_OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var state = _editorService.GetSelectionState();
        viewModel.UpdateEditorDebugState(
            state.IsBold,
            state.IsItalic,
            state.IsUnderline,
            state.IsStrikethrough,
            state.ParagraphAlignment.ToString(),
            state.HasSelection);
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

    private sealed class EditorService : ITextEditorService
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
        private bool _lastImportedFromDocx;

        public EditorService(RichTextBox editor)
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

        public bool TryWriteAutoSaveSnapshot(string path)
        {
            var textRange = new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            textRange.Save(stream, DataFormats.XamlPackage);
            return true;
        }

        public bool TryRestoreAutoSavedDocument(string path, out string? error)
        {
            error = null;
            try
            {
                var restored = new FlowDocument();
                var textRange = new TextRange(restored.ContentStart, restored.ContentEnd);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                textRange.Load(stream, DataFormats.XamlPackage);

                foreach (var key in _editor.Document.Resources.Keys)
                {
                    restored.Resources[key] = _editor.Document.Resources[key];
                }

                _editor.Document = restored;
                _currentFilePath = null;
                _hasUnsavedChanges = true;
                _lastImportedFromDocx = false;
                FormattingStateChanged?.Invoke();
                _editor.Focus();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool CanEdit => _editor.IsEnabled && !_editor.IsReadOnly;

        public bool CanUndo => _editor.CanUndo;

        public bool CanRedo => _editor.CanRedo;

        public bool HasUnsavedChanges => _hasUnsavedChanges;

        public string? CurrentFilePath => _currentFilePath;

        public void ApplyInlineProperty(DependencyProperty property, object value)
        {
            if (!CanEdit)
            {
                return;
            }

            _editor.Selection.ApplyPropertyValue(property, value);
            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
        }

        public void ApplyParagraphProperty(Action<Paragraph> apply)
        {
            if (!CanEdit)
            {
                return;
            }

            var paragraphs = EnumerateSelectedParagraphs(_editor.Selection).ToList();
            foreach (var paragraph in paragraphs)
            {
                apply(paragraph);
            }

            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
        }

        public EditorSelectionState GetSelectionState()
        {
            var selection = _editor.Selection;
            var isBold = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight fontWeight
                && fontWeight == FontWeights.Bold;
            var isItalic = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle fontStyle
                && fontStyle == FontStyles.Italic;

            var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            var isUnderline = decorations?.Any(x => x.Location == TextDecorationLocation.Underline) == true;
            var isStrike = decorations?.Any(x => x.Location == TextDecorationLocation.Strikethrough) == true;
            var alignment = selection.Start.Paragraph?.TextAlignment ?? TextAlignment.Left;

            return new EditorSelectionState(
                isBold,
                isItalic,
                isUnderline,
                isStrike,
                alignment,
                !selection.IsEmpty);
        }

        public void InsertBlock(Block block)
        {
            if (!CanEdit)
            {
                return;
            }

            var caretParagraph = _editor.CaretPosition.Paragraph;
            if (caretParagraph is null)
            {
                _editor.Document.Blocks.Add(block);
            }
            else
            {
                _editor.Document.Blocks.InsertAfter(caretParagraph, block);
            }

            _hasUnsavedChanges = true;
            FormattingStateChanged?.Invoke();
        }

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
                InsertBlock(divider);
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

        public bool ImportDocx(out string message)
        {
            message = string.Empty;

            if (!CanEdit)
            {
                message = "редактор недоступен";
                return false;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Импорт DOCX",
                Filter = "Word Document (*.docx)|*.docx",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                message = "пользователь отменил выбор файла";
                return false;
            }

            try
            {
                using var document = WordprocessingDocument.Open(dialog.FileName, false);
                var body = document.MainDocumentPart?.Document?.Body;
                if (body is null)
                {
                    message = "в файле не найдено тело документа";
                    return false;
                }

                var flow = new FlowDocument();
                foreach (var key in _editor.Document.Resources.Keys)
                {
                    flow.Resources[key] = _editor.Document.Resources[key];
                }

                foreach (var element in body.Elements())
                {
                    switch (element)
                    {
                        case W.Paragraph p:
                            flow.Blocks.Add(ConvertParagraph(p, document.MainDocumentPart));
                            break;
                        case W.Table table:
                            foreach (var row in table.Elements<W.TableRow>())
                            {
                                var cellTexts = row.Elements<W.TableCell>()
                                    .Select(c => string.Join(" ", c.Descendants<W.Text>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t))))
                                    .Where(t => !string.IsNullOrWhiteSpace(t));
                                var line = string.Join(" | ", cellTexts);
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    flow.Blocks.Add(new Paragraph(new Run(line)));
                                }
                            }

                            break;
                    }
                }

                if (!flow.Blocks.Any())
                {
                    flow.Blocks.Add(new Paragraph(new Run("")));
                }

                _editor.Document = flow;
                _currentFilePath = dialog.FileName;
                _hasUnsavedChanges = false;
                _lastImportedFromDocx = true;
                _editor.CaretPosition = _editor.Document.ContentStart;
                _editor.Selection.Select(_editor.CaretPosition, _editor.CaretPosition);
                FormattingStateChanged?.Invoke();
                _editor.Focus();

                message = $"загружен {Path.GetFileName(dialog.FileName)}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"ошибка импорта: {ex.Message}";
                return false;
            }
        }

        private static Paragraph ConvertParagraph(W.Paragraph source, MainDocumentPart? mainPart)
        {
            var paragraph = new Paragraph();
            var justification = source.ParagraphProperties?.Justification?.Val?.Value;
            paragraph.TextAlignment = justification switch
            {
                W.JustificationValues.Center => TextAlignment.Center,
                W.JustificationValues.Right => TextAlignment.Right,
                W.JustificationValues.Both => TextAlignment.Justify,
                _ => TextAlignment.Left
            };

            foreach (var child in source.ChildElements)
            {
                switch (child)
                {
                    case W.Run run:
                        AppendRunContent(paragraph.Inlines, run);
                        break;
                    case W.Hyperlink link:
                        AppendHyperlinkContent(paragraph.Inlines, link, mainPart);
                        break;
                }
            }

            if (!paragraph.Inlines.Any())
            {
                paragraph.Inlines.Add(new Run(string.Empty));
            }

            return paragraph;
        }

        private static void AppendHyperlinkContent(InlineCollection target, W.Hyperlink link, MainDocumentPart? mainPart)
        {
            Hyperlink? hyperlink = null;
            if (mainPart is not null && link.Id is not null)
            {
                try
                {
                    var relationship = mainPart.HyperlinkRelationships.FirstOrDefault(r => r.Id == link.Id);
                    if (relationship?.Uri is not null)
                    {
                        hyperlink = new Hyperlink { NavigateUri = relationship.Uri };
                    }
                }
                catch
                {
                    // ignore malformed hyperlinks in source docx and fallback to plain text import
                }
            }

            if (hyperlink is null)
            {
                foreach (var run in link.Elements<W.Run>())
                {
                    AppendRunContent(target, run);
                }

                return;
            }

            foreach (var run in link.Elements<W.Run>())
            {
                AppendRunContent(hyperlink.Inlines, run);
            }

            if (hyperlink.Inlines.Any())
            {
                target.Add(hyperlink);
            }
        }

        private static void AppendRunContent(InlineCollection target, W.Run sourceRun)
        {
            var props = sourceRun.RunProperties;
            var isBold = props?.Bold is not null;
            var isItalic = props?.Italic is not null;
            var hasUnderline = props?.Underline?.Val?.Value is not null and not W.UnderlineValues.None;
            var hasStrike = props?.Strike is not null || props?.DoubleStrike is not null;

            foreach (var item in sourceRun.ChildElements)
            {
                switch (item)
                {
                    case W.Text text:
                    {
                        var run = new Run(text.Text ?? string.Empty);
                        ApplyRunFormatting(run, isBold, isItalic, hasUnderline, hasStrike);
                        target.Add(run);
                        break;
                    }
                    case W.TabChar:
                    {
                        var run = new Run("	");
                        ApplyRunFormatting(run, isBold, isItalic, hasUnderline, hasStrike);
                        target.Add(run);
                        break;
                    }
                    case W.Break:
                    case W.CarriageReturn:
                        target.Add(new LineBreak());
                        break;
                }
            }
        }

        private static void ApplyRunFormatting(Run run, bool bold, bool italic, bool underline, bool strike)
        {
            if (bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (italic)
            {
                run.FontStyle = FontStyles.Italic;
            }

            if (!underline && !strike)
            {
                return;
            }

            run.TextDecorations = new TextDecorationCollection();
            if (underline)
            {
                run.TextDecorations.Add(TextDecorations.Underline[0].Clone());
            }

            if (strike)
            {
                run.TextDecorations.Add(TextDecorations.Strikethrough[0].Clone());
            }
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
            _lastImportedFromDocx = false;
            FormattingStateChanged?.Invoke();
            return true;
        }

        public bool ExportDocument(out string message)
        {
            message = string.Empty;

            if (!CanEdit)
            {
                message = "редактор недоступен";
                return false;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Экспорт документа",
                Filter = "Text (*.txt)|*.txt|HTML (*.html)|*.html|DOCX (*.docx)|*.docx|PDF (*.pdf)|*.pdf",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = string.IsNullOrWhiteSpace(_currentFilePath)
                    ? "document"
                    : Path.GetFileNameWithoutExtension(_currentFilePath)
            };

            if (dialog.ShowDialog() != true)
            {
                message = "пользователь отменил выбор файла";
                return false;
            }

            switch (dialog.FilterIndex)
            {
                case 1:
                    ExportAsText(dialog.FileName);
                    message = $"TXT → {Path.GetFileName(dialog.FileName)}";
                    return true;
                case 2:
                    ExportAsHtml(dialog.FileName);
                    message = $"HTML → {Path.GetFileName(dialog.FileName)}";
                    return true;
                case 3:
                    ExportAsDocx(dialog.FileName);
                    message = $"DOCX → {Path.GetFileName(dialog.FileName)}";
                    return true;
                case 4:
                    message = "PDF пока не реализован: печать FlowDocument через PrintDialog в Microsoft Print to PDF/XPS или интеграция библиотеки (например, Syncfusion/iText).";
                    MessageBox.Show(
                        "PDF-экспорт пока не реализован.\n\n"
                        + "Реалистичный путь:\n"
                        + "1) Печать FlowDocument через PrintDialog в Microsoft Print to PDF (или XPS).\n"
                        + "2) Либо экспорт через специализированную библиотеку (Syncfusion/iText).",
                        "Экспорт PDF",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return false;
                default:
                    message = "неподдерживаемый формат";
                    return false;
            }
        }

        private void ExportAsText(string fileName)
        {
            var textRange = new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd);
            File.WriteAllText(fileName, textRange.Text, Encoding.UTF8);
        }

        private void ExportAsHtml(string fileName)
        {
            var html = ConvertDocumentToSimpleHtml(_editor.Document);
            if (_lastImportedFromDocx)
            {
                html = sanitizeImportedHTML(html);
            }

            File.WriteAllText(fileName, html, Encoding.UTF8);
        }

        private void ExportAsDocx(string fileName)
        {
            using var wordDocument = WordprocessingDocument.Create(fileName, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
            var mainPart = wordDocument.AddMainDocumentPart();
            var body = new W.Body();

            foreach (var block in _editor.Document.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    body.Append(ConvertParagraphToWord(paragraph, mainPart));
                }
            }

            mainPart.Document = new W.Document(body);
            mainPart.Document.Save();
        }

        private static W.Paragraph ConvertParagraphToWord(Paragraph paragraph, MainDocumentPart mainPart)
        {
            var wordParagraph = new W.Paragraph();
            var properties = new W.ParagraphProperties();

            properties.Justification = paragraph.TextAlignment switch
            {
                TextAlignment.Center => new W.Justification { Val = W.JustificationValues.Center },
                TextAlignment.Right => new W.Justification { Val = W.JustificationValues.Right },
                TextAlignment.Justify => new W.Justification { Val = W.JustificationValues.Both },
                _ => new W.Justification { Val = W.JustificationValues.Left }
            };

            if (Math.Abs(paragraph.TextIndent) > 0.1)
            {
                var firstLineTwips = ((int)Math.Round(paragraph.TextIndent * 15d)).ToString(CultureInfo.InvariantCulture);
                properties.Indentation = new W.Indentation { FirstLine = firstLineTwips };
            }

            wordParagraph.Append(properties);

            foreach (var inline in paragraph.Inlines)
            {
                AppendInlineToWord(wordParagraph, inline, mainPart);
            }

            if (!wordParagraph.Elements<W.Run>().Any() && !wordParagraph.Elements<W.Hyperlink>().Any())
            {
                wordParagraph.Append(new W.Run(new W.Text(string.Empty)));
            }

            return wordParagraph;
        }

        private static void AppendInlineToWord(W.Paragraph targetParagraph, Inline inline, MainDocumentPart mainPart)
        {
            switch (inline)
            {
                case Run run:
                    targetParagraph.Append(CreateWordRun(run));
                    break;
                case LineBreak:
                    targetParagraph.Append(new W.Run(new W.Break()));
                    break;
                case Hyperlink hyperlink:
                {
                    var wordHyperlink = CreateWordHyperlink(hyperlink, mainPart);
                    targetParagraph.Append(wordHyperlink);
                    break;
                }
                case Span span:
                    foreach (var nested in span.Inlines)
                    {
                        AppendInlineToWord(targetParagraph, nested, mainPart);
                    }

                    break;
            }
        }

        private static W.Hyperlink CreateWordHyperlink(Hyperlink hyperlink, MainDocumentPart mainPart)
        {
            W.Hyperlink wordHyperlink;
            if (hyperlink.NavigateUri is not null)
            {
                var relationship = mainPart.AddHyperlinkRelationship(hyperlink.NavigateUri, true);
                wordHyperlink = new W.Hyperlink { Id = relationship.Id, History = DocumentFormat.OpenXml.OnOffValue.FromBoolean(true) };
            }
            else
            {
                wordHyperlink = new W.Hyperlink();
            }

            foreach (var nested in hyperlink.Inlines)
            {
                switch (nested)
                {
                    case Run run:
                        wordHyperlink.Append(CreateWordRun(run));
                        break;
                    case LineBreak:
                        wordHyperlink.Append(new W.Run(new W.Break()));
                        break;
                    case Span span:
                        foreach (var deep in span.Inlines.OfType<Run>())
                        {
                            wordHyperlink.Append(CreateWordRun(deep));
                        }

                        break;
                }
            }

            if (!wordHyperlink.Elements<W.Run>().Any())
            {
                wordHyperlink.Append(new W.Run(new W.Text(string.Empty)));
            }

            return wordHyperlink;
        }

        private static W.Run CreateWordRun(Run run)
        {
            var wordRun = new W.Run();
            var runProperties = new W.RunProperties();

            if (run.FontWeight == FontWeights.Bold)
            {
                runProperties.Append(new W.Bold());
            }

            if (run.FontStyle == FontStyles.Italic)
            {
                runProperties.Append(new W.Italic());
            }

            var decorations = run.TextDecorations;
            if (decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true)
            {
                runProperties.Append(new W.Underline { Val = W.UnderlineValues.Single });
            }

            if (decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true)
            {
                runProperties.Append(new W.Strike());
            }

            if (runProperties.ChildElements.Count > 0)
            {
                wordRun.Append(runProperties);
            }

            var text = run.Text ?? string.Empty;
            if (text.Length == 0)
            {
                wordRun.Append(new W.Text(string.Empty));
                return wordRun;
            }

            var buffer = new StringBuilder();
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '\t':
                        AppendBufferedWordText(wordRun, buffer);
                        wordRun.Append(new W.TabChar());
                        break;
                    case '\r':
                        break;
                    case '\n':
                        AppendBufferedWordText(wordRun, buffer);
                        wordRun.Append(new W.Break());
                        break;
                    default:
                        buffer.Append(ch);
                        break;
                }
            }

            AppendBufferedWordText(wordRun, buffer);
            return wordRun;
        }

        private static void AppendBufferedWordText(W.Run run, StringBuilder buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            run.Append(new W.Text(buffer.ToString()) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
            buffer.Clear();
        }

        private static string sanitizeImportedHTML(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return "<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>Export</title></head><body><p></p></body></html>";
            }

            var bodyMatch = System.Text.RegularExpressions.Regex.Match(
                html,
                @"<body[^>]*>(?<content>[\s\S]*?)</body>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var body = bodyMatch.Success ? bodyMatch.Groups["content"].Value : html;
            body = System.Text.RegularExpressions.Regex.Replace(
                body,
                @"<(script|style)\b[^>]*>[\s\S]*?</\1>",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            body = System.Text.RegularExpressions.Regex.Replace(body, @"<(\/?)b\b", "<$1strong", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"<(\/?)i\b", "<$1em", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"<(\/?)strike\b", "<$1s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"<(\/?)del\b", "<$1s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"<(\/?)div\b", "<$1p", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            body = System.Text.RegularExpressions.Regex.Replace(
                body,
                @"<p\b([^>]*)>",
                match =>
                {
                    var attrs = match.Groups[1].Value;
                    var align = "left";

                    var alignAttr = System.Text.RegularExpressions.Regex.Match(attrs, @"\balign\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (alignAttr.Success)
                    {
                        var rawAlign = alignAttr.Groups[1].Value.Trim('\"', '\'').ToLowerInvariant();
                        if (rawAlign is "left" or "center" or "right" or "justify")
                        {
                            align = rawAlign;
                        }
                    }
                    else
                    {
                        var styleAlign = System.Text.RegularExpressions.Regex.Match(attrs, @"text-align\s*:\s*(left|center|right|justify)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (styleAlign.Success)
                        {
                            align = styleAlign.Groups[1].Value.ToLowerInvariant();
                        }
                    }

                    return align == "left" ? "<p>" : $"<p style=\"text-align:{align};\">";
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            body = System.Text.RegularExpressions.Regex.Replace(
                body,
                @"<a\b([^>]*)>",
                match =>
                {
                    var attrs = match.Groups[1].Value;
                    var hrefMatch = System.Text.RegularExpressions.Regex.Match(attrs, @"\bhref\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!hrefMatch.Success)
                    {
                        return "<a>";
                    }

                    var href = hrefMatch.Groups[1].Value.Trim();
                    return $"<a href={href}>";
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            body = System.Text.RegularExpressions.Regex.Replace(body, @"<\/?(span|font|section|article|header|footer|main|aside)[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            body = System.Text.RegularExpressions.Regex.Replace(body, @"<(?!\/?(p|strong|em|u|s|a|br)\b)[^>]+>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"<p\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var trimmed = body.Trim();
                body = string.IsNullOrEmpty(trimmed) ? "<p></p>" : $"<p>{trimmed}</p>";
            }

            return $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>Export</title></head><body>{body}</body></html>";
        }

        private static string ConvertDocumentToSimpleHtml(FlowDocument document)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Export</title></head><body>");

            foreach (var block in document.Blocks)
            {
                if (block is not Paragraph paragraph)
                {
                    continue;
                }

                var align = paragraph.TextAlignment switch
                {
                    TextAlignment.Center => "center",
                    TextAlignment.Right => "right",
                    TextAlignment.Justify => "justify",
                    _ => "left"
                };

                sb.Append("<p style=\"text-align:").Append(align).Append(";\">");
                foreach (var inline in paragraph.Inlines)
                {
                    AppendInlineHtml(sb, inline);
                }

                sb.AppendLine("</p>");
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void AppendInlineHtml(StringBuilder sb, Inline inline)
        {
            switch (inline)
            {
                case Run run:
                {
                    var text = WebUtility.HtmlEncode(run.Text).Replace("\r\n", "<br/>").Replace("\n", "<br/>");

                    if (run.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true)
                    {
                        text = $"<s>{text}</s>";
                    }

                    if (run.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true)
                    {
                        text = $"<u>{text}</u>";
                    }

                    if (run.FontStyle == FontStyles.Italic)
                    {
                        text = $"<em>{text}</em>";
                    }

                    if (run.FontWeight == FontWeights.Bold)
                    {
                        text = $"<strong>{text}</strong>";
                    }

                    sb.Append(text);
                    break;
                }
                case LineBreak:
                    sb.Append("<br/>");
                    break;
                case Hyperlink hyperlink:
                {
                    var href = hyperlink.NavigateUri?.ToString() ?? "#";
                    sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">");
                    foreach (var nested in hyperlink.Inlines)
                    {
                        AppendInlineHtml(sb, nested);
                    }

                    sb.Append("</a>");
                    break;
                }
                case Span span:
                    foreach (var nested in span.Inlines)
                    {
                        AppendInlineHtml(sb, nested);
                    }

                    break;
            }
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

            var nextWeight = IsBoldActive ? FontWeights.Normal : FontWeights.Bold;
            ApplyInlineProperty(TextElement.FontWeightProperty, nextWeight);
            _editor.Focus();
        }

        public void ToggleItalic()
        {
            if (!CanEdit)
            {
                return;
            }

            var nextStyle = IsItalicActive ? FontStyles.Normal : FontStyles.Italic;
            ApplyInlineProperty(TextElement.FontStyleProperty, nextStyle);
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
            ApplyParagraphProperty(paragraph => paragraph.TextAlignment = alignment);
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

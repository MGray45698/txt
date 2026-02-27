using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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

    private sealed class RichTextBoxEditorService : ITextEditorService
    {
        private readonly RichTextBox _editor;

        public RichTextBoxEditorService(RichTextBox editor)
        {
            _editor = editor;
            _editor.SelectionChanged += (_, _) => FormattingStateChanged?.Invoke();
            _editor.TextChanged += (_, _) => FormattingStateChanged?.Invoke();
        }

        public event Action? FormattingStateChanged;

        public bool CanEdit => _editor.IsEnabled && !_editor.IsReadOnly;

        public bool CanUndo => _editor.CanUndo;

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

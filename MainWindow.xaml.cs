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
        }

        public event Action? FormattingStateChanged;

        public bool CanEdit => _editor.IsEnabled && !_editor.IsReadOnly;

        public bool IsBoldActive
        {
            get
            {
                var value = _editor.Selection.GetPropertyValue(TextElement.FontWeightProperty);
                return value is FontWeight fontWeight && fontWeight == FontWeights.Bold;
            }
        }

        public bool IsItalicActive
        {
            get
            {
                var value = _editor.Selection.GetPropertyValue(TextElement.FontStyleProperty);
                return value is FontStyle fontStyle && fontStyle == FontStyles.Italic;
            }
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
    }
}

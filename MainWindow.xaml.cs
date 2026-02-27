using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TxtEditorSkeleton.ViewModels;

namespace TxtEditorSkeleton;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
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
}

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using TxtEditorSkeleton.Commands;

namespace TxtEditorSkeleton.ViewModels;


public sealed record EditorSelectionState(
    bool IsBold,
    bool IsItalic,
    bool IsUnderline,
    bool IsStrikethrough,
    TextAlignment ParagraphAlignment,
    bool HasSelection);

public interface ITextEditorService
{
    event Action? FormattingStateChanged;

    bool CanEdit { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    bool HasUnsavedChanges { get; }
    string? CurrentFilePath { get; }
    bool IsBoldActive { get; }
    bool IsItalicActive { get; }
    bool IsUnderlineActive { get; }
    bool IsStrikethroughActive { get; }
    TextAlignment CurrentParagraphAlignment { get; }

    void ApplyInlineProperty(DependencyProperty property, object value);
    void ApplyParagraphProperty(Action<Paragraph> apply);
    EditorSelectionState GetSelectionState();
    void InsertBlock(Block block);

    bool SaveDocument();
    bool ImportDocx(out string message);
    bool ExportDocument(out string message);
    void Undo();
    void Redo();
    void ToggleBold();
    void ToggleItalic();
    void ToggleUnderline();
    void ToggleStrikethrough();
    void SetParagraphAlignment(TextAlignment alignment);
    void IncreaseParagraphIndent();
    void DecreaseParagraphIndent();
    void InsertFootnote();
    void RenumberFootnotes();
    void InsertDivider();
    bool HandleAtomicDelete(Key key);
}

public class MainViewModel : INotifyPropertyChanged
{
    private const string BaseWindowTitle = "WPF Text Editor Skeleton";

    private string _statusMessage = "Готово";
    private bool _isDocumentLoaded = true;
    private bool _isBoldAtCursor;
    private bool _isItalicAtCursor;
    private bool _isUnderlineAtCursor;
    private bool _isStrikeAtCursor;
    private bool _hasSelection;
    private string _paragraphAlignment = "Left";

    private readonly RelayCommand _newCommand;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _importCommand;
    private readonly RelayCommand _exportCommand;
    private readonly RelayCommand _undoCommand;
    private readonly RelayCommand _redoCommand;
    private readonly RelayCommand _footnoteCommand;
    private readonly RelayCommand _dividerCommand;
    private readonly RelayCommand _boldCommand;
    private readonly RelayCommand _italicCommand;
    private readonly RelayCommand _underlineCommand;
    private readonly RelayCommand _strikethroughCommand;
    private readonly RelayCommand _alignLeftCommand;
    private readonly RelayCommand _alignCenterCommand;
    private readonly RelayCommand _alignRightCommand;
    private ITextEditorService? _editorService;
    private bool _isBoldActive;
    private bool _isItalicActive;
    private bool _isUnderlineActive;
    private bool _isStrikethroughActive;
    private bool _isAlignLeftActive;
    private bool _isAlignCenterActive;
    private bool _isAlignRightActive;
    private bool _canUndo;
    private bool _canRedo;
    private bool _hasUnsavedChanges;
    private string? _currentFilePath;

    public MainViewModel()
    {
        _newCommand = new RelayCommand(_ => ExecuteAction("Создать новый документ"));
        _saveCommand = new RelayCommand(_ => ExecuteSave(), _ => CanExecuteSave());
        _importCommand = new RelayCommand(_ => ExecuteImport(), _ => CanExecuteSave());
        _exportCommand = new RelayCommand(_ => ExecuteExport(), _ => CanExecuteSave());
        _undoCommand = new RelayCommand(_ => ExecuteUndo(), _ => CanExecuteUndo());
        _redoCommand = new RelayCommand(_ => ExecuteRedo(), _ => CanExecuteRedo());
        _footnoteCommand = new RelayCommand(_ => ExecuteFootnote(), _ => CanExecuteTextFormat());
        _dividerCommand = new RelayCommand(_ => ExecuteDivider(), _ => CanExecuteTextFormat());
        _boldCommand = new RelayCommand(_ => ExecuteBold(), _ => CanExecuteTextFormat());
        _italicCommand = new RelayCommand(_ => ExecuteItalic(), _ => CanExecuteTextFormat());
        _underlineCommand = new RelayCommand(_ => ExecuteUnderline(), _ => CanExecuteTextFormat());
        _strikethroughCommand = new RelayCommand(_ => ExecuteStrikethrough(), _ => CanExecuteTextFormat());
        _alignLeftCommand = new RelayCommand(_ => ExecuteAlignLeft(), _ => CanExecuteTextFormat());
        _alignCenterCommand = new RelayCommand(_ => ExecuteAlignCenter(), _ => CanExecuteTextFormat());
        _alignRightCommand = new RelayCommand(_ => ExecuteAlignRight(), _ => CanExecuteTextFormat());

        NewCommand = _newCommand;
        SaveCommand = _saveCommand;
        ImportCommand = _importCommand;
        ExportCommand = _exportCommand;
        UndoCommand = _undoCommand;
        RedoCommand = _redoCommand;
        FootnoteCommand = _footnoteCommand;
        DividerCommand = _dividerCommand;
        BoldCommand = _boldCommand;
        ItalicCommand = _italicCommand;
        UnderlineCommand = _underlineCommand;
        StrikethroughCommand = _strikethroughCommand;
        AlignLeftCommand = _alignLeftCommand;
        AlignCenterCommand = _alignCenterCommand;
        AlignRightCommand = _alignRightCommand;
        ToggleDocumentStateCommand = new RelayCommand(_ => IsDocumentLoaded = !IsDocumentLoaded);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand FootnoteCommand { get; }
    public ICommand DividerCommand { get; }
    public ICommand BoldCommand { get; }
    public ICommand ItalicCommand { get; }
    public ICommand UnderlineCommand { get; }
    public ICommand StrikethroughCommand { get; }
    public ICommand AlignLeftCommand { get; }
    public ICommand AlignCenterCommand { get; }
    public ICommand AlignRightCommand { get; }

    public ICommand ToggleDocumentStateCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string UndoStateLabel => CanUndo ? "Undo доступен" : "Undo недоступен";
    public string RedoStateLabel => CanRedo ? "Redo доступен" : "Redo недоступен";
    public string DirtyStateLabel => HasUnsavedChanges ? "Изменения: не сохранены" : "Изменения: сохранены";

    public string WindowTitle
    {
        get
        {
            var filePart = string.IsNullOrWhiteSpace(CurrentFilePath)
                ? BaseWindowTitle
                : $"{Path.GetFileName(CurrentFilePath)} — {BaseWindowTitle}";
            return HasUnsavedChanges ? $"{filePart} *" : filePart;
        }
    }

    public bool IsDocumentLoaded
    {
        get => _isDocumentLoaded;
        set
        {
            if (_isDocumentLoaded == value)
            {
                return;
            }

            _isDocumentLoaded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DocumentStateLabel));
            UpdateCommandStates();
        }
    }

    public bool CanUndo
    {
        get => _canUndo;
        private set
        {
            if (_canUndo == value)
            {
                return;
            }

            _canUndo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UndoStateLabel));
        }
    }

    public bool CanRedo
    {
        get => _canRedo;
        private set
        {
            if (_canRedo == value)
            {
                return;
            }

            _canRedo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RedoStateLabel));
        }
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (_hasUnsavedChanges == value)
            {
                return;
            }

            _hasUnsavedChanges = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(DirtyStateLabel));
        }
    }

    public string? CurrentFilePath
    {
        get => _currentFilePath;
        private set
        {
            if (_currentFilePath == value)
            {
                return;
            }

            _currentFilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(DirtyStateLabel));
        }
    }

    public bool IsBoldActive
    {
        get => _isBoldActive;
        private set => SetStateField(ref _isBoldActive, value);
    }

    public bool IsItalicActive
    {
        get => _isItalicActive;
        private set => SetStateField(ref _isItalicActive, value);
    }

    public bool IsUnderlineActive
    {
        get => _isUnderlineActive;
        private set => SetStateField(ref _isUnderlineActive, value);
    }

    public bool IsStrikethroughActive
    {
        get => _isStrikethroughActive;
        private set => SetStateField(ref _isStrikethroughActive, value);
    }

    public bool IsAlignLeftActive
    {
        get => _isAlignLeftActive;
        private set => SetStateField(ref _isAlignLeftActive, value);
    }

    public bool IsAlignCenterActive
    {
        get => _isAlignCenterActive;
        private set => SetStateField(ref _isAlignCenterActive, value);
    }

    public bool IsAlignRightActive
    {
        get => _isAlignRightActive;
        private set => SetStateField(ref _isAlignRightActive, value);
    }

    public bool IsBoldAtCursor
    {
        get => _isBoldAtCursor;
        private set => SetDebugStateField(ref _isBoldAtCursor, value);
    }

    public bool IsItalicAtCursor
    {
        get => _isItalicAtCursor;
        private set => SetDebugStateField(ref _isItalicAtCursor, value);
    }

    public bool IsUnderlineAtCursor
    {
        get => _isUnderlineAtCursor;
        private set => SetDebugStateField(ref _isUnderlineAtCursor, value);
    }

    public bool IsStrikeAtCursor
    {
        get => _isStrikeAtCursor;
        private set => SetDebugStateField(ref _isStrikeAtCursor, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetDebugStateField(ref _hasSelection, value);
    }

    public string ParagraphAlignment
    {
        get => _paragraphAlignment;
        private set => SetDebugStateField(ref _paragraphAlignment, value);
    }

    public string DocumentStateLabel => IsDocumentLoaded ? "Документ активен" : "Документ отключен";

    public string DebugPanelText =>
        $"Debug: Bold={IsBoldAtCursor}, Italic={IsItalicAtCursor}, Underline={IsUnderlineAtCursor}, Strike={IsStrikeAtCursor}, Align={ParagraphAlignment}, Selection={HasSelection}";

    public void UpdateEditorDebugState(
        bool isBold,
        bool isItalic,
        bool isUnderline,
        bool isStrike,
        string paragraphAlignment,
        bool hasSelection)
    {
        IsBoldAtCursor = isBold;
        IsItalicAtCursor = isItalic;
        IsUnderlineAtCursor = isUnderline;
        IsStrikeAtCursor = isStrike;
        ParagraphAlignment = paragraphAlignment;
        HasSelection = hasSelection;
    }


    public void ApplyParagraphIndent(bool decrease)
    {
        if (decrease)
        {
            _editorService?.DecreaseParagraphIndent();
            ExecuteAction("Уменьшить отступ первой строки");
            return;
        }

        _editorService?.IncreaseParagraphIndent();
        ExecuteAction("Увеличить отступ первой строки");
    }

    public bool ApplyAtomicDelete(Key key)
    {
        return _editorService?.HandleAtomicDelete(key) == true;
    }

    public void AttachEditorService(ITextEditorService editorService)
    {
        _editorService = editorService;
        _editorService.FormattingStateChanged += OnEditorFormattingChanged;
        UpdateTextFormattingStateFromEditor();
        UpdateCommandStates();
    }

    private void ExecuteAction(string actionName)
    {
        StatusMessage = $"{actionName} ({DateTime.Now:HH:mm:ss})";
    }

    private void UpdateCommandStates()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _importCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
        _undoCommand.RaiseCanExecuteChanged();
        _redoCommand.RaiseCanExecuteChanged();
        _footnoteCommand.RaiseCanExecuteChanged();
        _dividerCommand.RaiseCanExecuteChanged();
        _boldCommand.RaiseCanExecuteChanged();
        _italicCommand.RaiseCanExecuteChanged();
        _underlineCommand.RaiseCanExecuteChanged();
        _strikethroughCommand.RaiseCanExecuteChanged();
        _alignLeftCommand.RaiseCanExecuteChanged();
        _alignCenterCommand.RaiseCanExecuteChanged();
        _alignRightCommand.RaiseCanExecuteChanged();
    }

    private void ExecuteSave()
    {
        if (_editorService?.SaveDocument() == true)
        {
            ExecuteAction("Документ сохранён");
        }
        else
        {
            ExecuteAction("Сохранение отменено");
        }
    }

    private bool CanExecuteSave() => IsDocumentLoaded && _editorService?.CanEdit == true;

    private void ExecuteImport()
    {
        if (_editorService?.ImportDocx(out var message) == true)
        {
            ExecuteAction($"Импорт DOCX: {message}");
        }
        else
        {
            ExecuteAction($"Импорт DOCX отменён/ошибка: {message ?? "без изменений"}");
        }
    }

    private void ExecuteExport()
    {
        if (_editorService?.ExportDocument(out var message) == true)
        {
            ExecuteAction($"Экспорт: {message}");
        }
        else
        {
            ExecuteAction($"Экспорт отменён: {message ?? "без изменений"}");
        }
    }

    private void ExecuteUndo()
    {
        _editorService?.Undo();
        ExecuteAction("Отмена действия");
    }

    private bool CanExecuteUndo() => IsDocumentLoaded && _editorService?.CanUndo == true;

    private void ExecuteRedo()
    {
        _editorService?.Redo();
        ExecuteAction("Повтор действия");
    }

    private bool CanExecuteRedo() => IsDocumentLoaded && _editorService?.CanRedo == true;

    private void ExecuteFootnote()
    {
        _editorService?.InsertFootnote();
        ExecuteAction("Вставить сноску");
    }

    private void ExecuteDivider()
    {
        _editorService?.InsertDivider();
        ExecuteAction("Вставить разделитель");
    }

    private void ExecuteBold()
    {
        _editorService?.ToggleBold();
        ExecuteAction("Переключить жирный текст");
    }

    private bool CanExecuteTextFormat() => IsDocumentLoaded && _editorService?.CanEdit == true;

    private void OnEditorFormattingChanged()
    {
        UpdateTextFormattingStateFromEditor();
        UpdateCommandStates();
    }

    private void ExecuteItalic()
    {
        _editorService?.ToggleItalic();
        ExecuteAction("Переключить курсив");
    }

    private void ExecuteUnderline()
    {
        _editorService?.ToggleUnderline();
        ExecuteAction("Переключить подчеркивание");
    }

    private void ExecuteStrikethrough()
    {
        _editorService?.ToggleStrikethrough();
        ExecuteAction("Переключить зачёркивание");
    }

    private void ExecuteAlignLeft()
    {
        _editorService?.SetParagraphAlignment(TextAlignment.Left);
        ExecuteAction("Выравнивание по левому краю");
    }

    private void ExecuteAlignCenter()
    {
        _editorService?.SetParagraphAlignment(TextAlignment.Center);
        ExecuteAction("Выравнивание по центру");
    }

    private void ExecuteAlignRight()
    {
        _editorService?.SetParagraphAlignment(TextAlignment.Right);
        ExecuteAction("Выравнивание по правому краю");
    }

    private void UpdateTextFormattingStateFromEditor()
    {
        var selectionState = _editorService?.GetSelectionState();

        IsBoldActive = selectionState?.IsBold ?? false;
        IsItalicActive = selectionState?.IsItalic ?? false;
        IsUnderlineActive = selectionState?.IsUnderline ?? false;
        IsStrikethroughActive = selectionState?.IsStrikethrough ?? false;
        CanUndo = _editorService?.CanUndo == true;
        CanRedo = _editorService?.CanRedo == true;
        HasUnsavedChanges = _editorService?.HasUnsavedChanges == true;
        CurrentFilePath = _editorService?.CurrentFilePath;

        var alignment = selectionState?.ParagraphAlignment ?? TextAlignment.Left;
        IsAlignLeftActive = alignment == TextAlignment.Left;
        IsAlignCenterActive = alignment == TextAlignment.Center;
        IsAlignRightActive = alignment == TextAlignment.Right;
    }

    private void SetStateField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void SetDebugStateField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(DebugPanelText));
    }

    private void SetDebugStateField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(DebugPanelText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

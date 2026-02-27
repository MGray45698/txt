# Структура классов (текущая архитектура)

## UI слой
- `MainWindow` (`MainWindow.xaml` / `MainWindow.xaml.cs`)
  - отвечает за визуальные события окна (Loaded/Closing, SelectionChanged, PreviewKeyDown)
  - держит экземпляр `EditorService`
  - делегирует операции редактирования в `EditorService`

## MVVM слой
- `MainViewModel` (`ViewModels/MainViewModel.cs`)
  - команды UI (`Save/Import/Export`, форматирование, выравнивание, undo/redo и т.д.)
  - состояние UI (`WindowTitle`, dirty/undo/redo labels, debug state)
  - не работает напрямую с `RichTextBox`, вызывает только `ITextEditorService`

- `RelayCommand` (`Commands/RelayCommand.cs`)
  - простая реализация `ICommand`

## Сервисный слой
- `ITextEditorService` (`ViewModels/MainViewModel.cs`)
  - контракт между ViewModel и редактором
  - базовые операции документа/форматирования/импорта/экспорта
  - унифицированные методы:
    - `ApplyInlineProperty(...)`
    - `ApplyParagraphProperty(...)`
    - `GetSelectionState()`
    - `InsertBlock(...)`

- `EditorService` (внутренний класс в `MainWindow.xaml.cs`)
  - единственная точка работы с `RichTextBox` и `FlowDocument`
  - реализует:
    - inline/paragraph форматирование
    - вставку блоков, divider и сносок
    - undo/redo
    - import/export (DOCX/TXT/HTML)
    - autosave/restore snapshot

## DTO состояния выделения
- `EditorSelectionState` (`ViewModels/MainViewModel.cs`)
  - переносит состояние форматирования/выравнивания/selection
  - используется ViewModel для синхронизации toggle-состояний

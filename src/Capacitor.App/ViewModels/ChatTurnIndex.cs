namespace Capacitor.App.ViewModels;

/// Turn starts also bound the preceding interval. Folded starts share the displayed header's position.
internal sealed class ChatTurnIndex {
    readonly List<ChatTurnDestination> _starts = [];
    int _number;
    bool _hasPrompt;
    bool _inOutput;
    ChatTurnDestination? _firstPrompt;
    ChatTurnDestination? _latestPrompt;

    public ChatTurnDestination First => _firstPrompt ?? _starts[0];
    public ChatTurnDestination Latest => _latestPrompt ?? _starts[^1];

    public IReadOnlyList<ChatTurnDestination> Starts => _starts;
    public int IndexedRows { get; private set; }

    public void Reset() {
        _starts.Clear();
        IndexedRows = 0;
        _number = 0;
        _hasPrompt = false;
        _inOutput = false;
        _firstPrompt = _latestPrompt = null;
    }

    public void Append(IReadOnlyList<ChatItemViewModel> rows, bool hasOlder) {
        for (; IndexedRows < rows.Count; IndexedRows++) {
            var row = rows[IndexedRows];
            if (row is ChatTopicItem { IsExpanded: false } fold) {
                foreach (var hidden in fold.Hidden) Add(hidden, folded: true, hasOlder);
            } else Add(row, folded: false, hasOlder);
        }
    }

    void Add(ChatItemViewModel row, bool folded, bool hasOlder) {
        switch (row) {
            case ChatTopicItem or ChatTopicEndItem or PendingCardItem:
                return;
            case UserTurnItem { IsUndelivered: false }:
                _latestPrompt = new(row, IndexedRows, ++_number, folded, Partial: false);
                _firstPrompt ??= _latestPrompt;
                _starts.Add(_latestPrompt);
                _hasPrompt = _inOutput = true;
                break;
            case ShellCommandItem { HasCommand: true } or UserTurnItem:
                _starts.Add(new(row, IndexedRows, 0, folded, Partial: false));
                _inOutput = false;
                break;
            case SystemNoteItem when !_inOutput:
                _starts.Add(new(row, IndexedRows, 0, folded, Partial: false));
                break;
            default:
                if (!_inOutput) _starts.Add(new(row, IndexedRows, 0, folded, Partial: !_hasPrompt && hasOlder));
                _inOutput = true;
                break;
        }
    }

    public int LowerBound(int row) {
        var left = 0;
        var right = _starts.Count;
        while (left < right) {
            var middle = left + (right - left) / 2;
            if (_starts[middle].ProjectedIndex < row) left = middle + 1;
            else right = middle;
        }
        return left;
    }
}

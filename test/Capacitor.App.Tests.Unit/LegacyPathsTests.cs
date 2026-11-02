using System.Text;
using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Tests.Unit;

public class LegacyPathsTests {
    const string Root = @"C:\Users\u\AppData\Local\KurrentCapacitor";

    [Test]
    [Arguments(@"""C:\Users\u\AppData\Local\KurrentCapacitor\Update.exe"" --uninstall", true)]
    [Arguments(@"c:\users\U\appdata\local\kurrentcapacitor", true)]
    [Arguments(@"C:\Users\u\AppData\Local\KurrentCapacitor2\Update.exe", false)]
    [Arguments(@"C:\Users\u\AppData\Local\kcap\Update.exe", false)]
    public async Task A_mention_ends_where_the_root_does(string text, bool expected) {
        await Assert.That(LegacyPaths.Mentions(text, Root)).IsEqualTo(expected);
    }

    [Test]
    public async Task Rebase_moves_every_mention_and_keeps_the_rest() {
        var rewritten = LegacyPaths.Rebase(
            @"""C:\Users\u\AppData\Local\KurrentCapacitor\Update.exe"" --uninstall --dir C:\Users\u\AppData\Local\KurrentCapacitor2",
            Root + @"\", @"C:\Users\u\AppData\Local\kcap");

        await Assert.That(rewritten).IsEqualTo(
            @"""C:\Users\u\AppData\Local\kcap\Update.exe"" --uninstall --dir C:\Users\u\AppData\Local\KurrentCapacitor2");
    }

    /// <summary>A shortcut's strings sit at whatever offset the structures before them leave, so the UTF-16 copy of
    /// the target can start on an odd byte.</summary>
    [Test]
    [Arguments(4, true)]
    [Arguments(5, true)]
    [Arguments(5, false)]
    public async Task A_shortcut_names_the_root_in_either_text_encoding(int header, bool unicode) {
        var target = Root + @"\current\kcap-desktop.exe" + "\0";
        var lnk    = new byte[header].Concat(unicode ? Encoding.Unicode.GetBytes(target) : Encoding.Latin1.GetBytes(target)).ToArray();

        await Assert.That(LegacyPaths.ShortcutMentions(lnk, Root)).IsTrue();
        await Assert.That(LegacyPaths.ShortcutMentions(lnk, @"C:\Users\u\AppData\Local\kcap")).IsFalse();
    }
}

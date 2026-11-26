// Usage: type TEXT | type --key CODE   Posts unicode keystrokes (or one virtual key) through the HID event tap.
import CoreGraphics
import Foundation

let args = CommandLine.arguments
func post(_ code: CGKeyCode, _ text: String?) {
    for down in [true, false] {
        let e = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)!
        if let t = text { let u = Array(t.utf16); e.keyboardSetUnicodeString(stringLength: u.count, unicodeString: u) }
        e.post(tap: .cghidEventTap)
        usleep(15_000)
    }
}
if args.count == 3, args[1] == "--key", let c = UInt16(args[2]) { post(c, nil); exit(0) }
guard args.count == 2 else { exit(2) }
for ch in args[1] { post(0, String(ch)) }

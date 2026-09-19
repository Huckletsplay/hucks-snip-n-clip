import AppKit

/// A native checkbox or radio control hosted inside an NSMenuItem. AppKit sends mouse events to a
/// menu item's custom view without ending menu tracking, so several routine settings can be changed
/// during one visit to the menu. The NSMenuItem still keeps its ordinary title/action for type
/// selection and accessibility metadata.
final class PersistentMenuChoiceView: NSView {
    enum Style {
        case checkbox
        case radio
    }

    private let button: NSButton
    private let selectionProvider: () -> Bool
    private let enabledProvider: () -> Bool
    private let activation: () -> Void

    init(
        title: String,
        style: Style,
        isSelected: @escaping () -> Bool,
        isEnabled: @escaping () -> Bool = { true },
        activation: @escaping () -> Void
    ) {
        button = style == .radio
            ? NSButton(radioButtonWithTitle: title, target: nil, action: nil)
            : NSButton(checkboxWithTitle: title, target: nil, action: nil)
        selectionProvider = isSelected
        enabledProvider = isEnabled
        self.activation = activation

        let font = NSFont.menuFont(ofSize: 0)
        button.font = font
        button.alignment = .left
        button.focusRingType = .none
        let titleWidth = ceil((title as NSString).size(withAttributes: [.font: font]).width)
        let width = max(250, min(540, titleWidth + 44))
        super.init(frame: NSRect(x: 0, y: 0, width: width, height: 24))

        button.frame = bounds.insetBy(dx: 6, dy: 1)
        button.autoresizingMask = [.width, .height]
        button.target = self
        button.action = #selector(activate)
        addSubview(button)
        refresh()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func refresh() {
        button.state = selectionProvider() ? .on : .off
        button.isEnabled = enabledProvider()
    }

    @objc private func activate() {
        guard enabledProvider() else {
            refresh()
            return
        }
        activation()
        refresh()
    }

    // The headless suite verifies the state/action contract without opening a visible menu.
    func activateForTesting() {
        activate()
    }

    var displayedStateForTesting: NSControl.StateValue {
        button.state
    }

    var isEnabledForTesting: Bool {
        button.isEnabled
    }
}

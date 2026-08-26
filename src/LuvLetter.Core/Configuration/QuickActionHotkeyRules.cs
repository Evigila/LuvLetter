using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public enum QuickActionHotkeyConflict
{
    None,
    DuplicateAction,
    ItemActivationKey,
}

public static class QuickActionHotkeyRules
{
    public static QuickActionHotkeyConflict FindConflict(
        QuickActionsHotkeyOptions hotkeys,
        int itemsPerPage)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemsPerPage, 1);

        if (AreEqual(hotkeys.PreviousPage, hotkeys.NextPage)
            || AreEqual(hotkeys.PreviousPage, hotkeys.Cancel)
            || AreEqual(hotkeys.NextPage, hotkeys.Cancel))
        {
            return QuickActionHotkeyConflict.DuplicateAction;
        }

        return ConflictsWithItemKeys(
                hotkeys.PreviousPage,
                hotkeys.FirstItemVirtualKey,
                itemsPerPage)
            || ConflictsWithItemKeys(
                hotkeys.NextPage,
                hotkeys.FirstItemVirtualKey,
                itemsPerPage)
            || ConflictsWithItemKeys(
                hotkeys.Cancel,
                hotkeys.FirstItemVirtualKey,
                itemsPerPage)
            ? QuickActionHotkeyConflict.ItemActivationKey
            : QuickActionHotkeyConflict.None;
    }

    private static bool AreEqual(HotkeyDefinition left, HotkeyDefinition right) =>
        left.VirtualKey == right.VirtualKey && left.Modifiers == right.Modifiers;

    private static bool ConflictsWithItemKeys(
        HotkeyDefinition hotkey,
        int firstItemVirtualKey,
        int itemsPerPage)
    {
        if (hotkey.Modifiers != HotkeyModifierKeys.None)
        {
            return false;
        }

        if (hotkey.VirtualKey >= firstItemVirtualKey
            && hotkey.VirtualKey < firstItemVirtualKey + itemsPerPage)
        {
            return true;
        }

        const int topRowDigitZero = 0x30;
        const int topRowDigitNine = 0x39;
        const int numpadDigitZero = 0x60;
        if (firstItemVirtualKey is < topRowDigitZero or > topRowDigitNine)
        {
            return false;
        }

        var firstNumpadKey = numpadDigitZero + firstItemVirtualKey - topRowDigitZero;
        return hotkey.VirtualKey >= firstNumpadKey
            && hotkey.VirtualKey < firstNumpadKey + itemsPerPage;
    }
}

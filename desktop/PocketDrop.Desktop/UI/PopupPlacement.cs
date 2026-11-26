using System.Windows;

namespace PocketDrop.UI
{
    internal static class PopupPlacement
    {
        // The popups already have a 12px transparent margin for their shadow.
        public const double Margin = 4;

        /// <summary>Puts a window in the top-right corner of the primary work area.</summary>
        public static void TopRight(Window w)
        {
            var area = SystemParameters.WorkArea;
            w.Left = area.Right - w.ActualWidth - Margin;
            w.Top = area.Top + Margin;
        }
    }
}

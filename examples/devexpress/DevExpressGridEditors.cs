#if WINFORMS_MCP_INSTRUMENTED
using System;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Forms;
using System.Xml;
using DevExpress.Utils;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraEditors.Repository;
using WinFormsMcp.Instrumented.Grids;

namespace WinFormsMcp.Examples.Adapters
{
    /// <summary>
    /// How the text an agent typed becomes the value of a grid's in-place editor. The decision is made from the
    /// editor that is actually active (<see cref="BaseEdit.Properties"/>), not from the column, because an
    /// application can swap the editor per row. Runs on the UI thread. Messages are fixed texts: they never contain
    /// the typed text.
    /// </summary>
    internal static class DevExpressGridEditors
    {
        // ISO 8601 date and date-time forms; "K" accepts a Z or an offset.
        private static readonly string[] IsoDateFormats =
        {
            "yyyy-MM-dd",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
            "yyyy-MM-ddTHH:mmK",
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"
        };

        /// <summary>True when the editor shows its text as dots or stars: such a column is redacted.</summary>
        internal static bool IsRedacted(RepositoryItem edit)
        {
            var text = edit as RepositoryItemTextEdit;
            return text != null && (text.PasswordChar != '\0' || text.UseSystemPasswordChar);
        }

        /// <summary>
        /// Puts the typed text into the active editor and marks the editor modified, so that posting writes it. On
        /// false nothing was set and <paramref name="reason"/> and <paramref name="message"/> say why.
        /// </summary>
        internal static bool TryApply(BaseEdit editor, string text, out GridEditRejection reason, out string message)
        {
            reason = GridEditRejection.Other;
            message = null;
            RepositoryItem properties = editor.Properties;
            object value;

            // The most derived editors first: the time-span editor derives from the time editor, the MRU editor and
            // the image and font combo boxes from the combo box.
            if (properties is RepositoryItemCheckEdit)
            {
                var check = (RepositoryItemCheckEdit)properties;
                switch (text.Trim().ToLowerInvariant())
                {
                    case "true":
                    case "1":
                    case "checked":
                        value = check.ValueChecked;
                        break;
                    case "false":
                    case "0":
                    case "unchecked":
                        value = check.ValueUnchecked;
                        break;
                    default:
                        return Refuse(GridEditRejection.InvalidInput, "The check box takes true, false, 1, 0, checked or unchecked.", out reason, out message);
                }
            }
            else if (properties is RepositoryItemDateEdit)
            {
                if (text.Length == 0)
                {
                    if (!AcceptsNull(properties))
                        return Refuse(GridEditRejection.InvalidInput, "The editor does not accept an empty value.", out reason, out message);
                    value = null;
                }
                else
                {
                    DateTime date;
                    if (!TryParseIsoDate(text, out date)
                        && !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                        return Refuse(GridEditRejection.InvalidInput, "The text is not a date.", out reason, out message);
                    value = date;
                }
            }
            else if (properties is RepositoryItemTimeSpanEdit)
            {
                if (text.Length == 0)
                {
                    if (!AcceptsNull(properties))
                        return Refuse(GridEditRejection.InvalidInput, "The editor does not accept an empty value.", out reason, out message);
                    value = null;
                }
                else
                {
                    TimeSpan span;
                    if (!TryParseIsoDuration(text, out span)
                        && !TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out span))
                        return Refuse(GridEditRejection.InvalidInput, "The text is not a duration.", out reason, out message);
                    value = span;
                }
            }
            else if (properties is RepositoryItemLookUpEdit)
            {
                var lookup = (RepositoryItemLookUpEdit)properties;
                if (text.Length == 0)
                {
                    if (!AcceptsNull(properties))
                        return Refuse(GridEditRejection.InvalidInput, "The editor does not accept an empty value.", out reason, out message);
                    value = null;
                }
                else
                {
                    value = lookup.GetKeyValueByDisplayText(text);
                    if (value == null)
                        return Refuse(GridEditRejection.InvalidInput, "No choice has this text.", out reason, out message);
                    if (HasAnotherLookUpMatch(lookup, text))
                        return Refuse(GridEditRejection.AmbiguousInput, "Several choices have this text.", out reason, out message);
                }
            }
            else if (properties is RepositoryItemImageComboBox || properties is RepositoryItemFontEdit)
            {
                return Refuse(GridEditRejection.Other, "The editor type is not supported.", out reason, out message);
            }
            else if (properties is RepositoryItemComboBox && !(properties is RepositoryItemMRUEdit))
            {
                object match = null;
                int matches = 0;
                foreach (object item in ((RepositoryItemComboBox)properties).Items)
                {
                    string itemText = item == null ? string.Empty : (item.ToString() ?? string.Empty);
                    if (!string.Equals(itemText, text, StringComparison.Ordinal)) continue;
                    match = item;
                    matches++;
                }
                if (matches > 1)
                    return Refuse(GridEditRejection.AmbiguousInput, "Several choices have this text.", out reason, out message);
                if (matches == 0 && text.Length != 0)
                    return Refuse(GridEditRejection.InvalidInput, "No choice has this text.", out reason, out message);
                if (matches == 0 && !AcceptsNull(properties))
                    return Refuse(GridEditRejection.InvalidInput, "No choice has this text.", out reason, out message);
                value = match;
            }
            else if (properties is RepositoryItemSpinEdit || properties is RepositoryItemCalcEdit)
            {
                // Observed: a spin editor given text that is not a number does not refuse it, it shows 0 and posts 0. So
                // the text is parsed here, the way a user's typing is, and only a number reaches the editor.
                if (text.Trim().Length == 0)
                {
                    if (!AcceptsNull(properties))
                        return Refuse(GridEditRejection.InvalidInput, "The editor does not accept an empty value.", out reason, out message);
                    value = null;
                }
                else
                {
                    decimal number;
                    if (!decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out number))
                        return Refuse(GridEditRejection.InvalidInput, "The text is not a number.", out reason, out message);
                    value = number;
                }
            }
            else if (properties is RepositoryItemTimeEdit)
            {
                // The time editor's value is a date and time; the typed time replaces the time of the shown value.
                if (text.Trim().Length == 0)
                {
                    if (!AcceptsNull(properties))
                        return Refuse(GridEditRejection.InvalidInput, "The editor does not accept an empty value.", out reason, out message);
                    value = null;
                }
                else
                {
                    DateTime time;
                    if (!DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out time))
                        return Refuse(GridEditRejection.InvalidInput, "The text is not a time.", out reason, out message);
                    DateTime shown = editor.EditValue is DateTime ? (DateTime)editor.EditValue : DateTime.MinValue;
                    value = shown.Date + time.TimeOfDay;
                }
            }
            else if (IsTextLike(properties))
            {
                // A button editor that does not take typing (the text box is disabled or read-only by style) is not
                // typed into: its value comes from the button.
                var button = properties as RepositoryItemButtonEdit;
                if (button != null && button.TextEditStyle != TextEditStyles.Standard)
                    return Refuse(GridEditRejection.NotEditable, "The editor does not take typed text.", out reason, out message);
                editor.Text = text;
                editor.IsModified = true;
                return true;
            }
            else
            {
                return Refuse(GridEditRejection.Other, "The editor type is not supported.", out reason, out message);
            }

            editor.EditValue = value;
            editor.IsModified = true;
            return true;
        }

        // Text, memo, button, MRU and hyperlink editors take the typed text as it is. The popup editors (color, popup
        // container, checked combo box, ...) derive from the button editor but are not text: they stay unsupported.
        private static bool IsTextLike(RepositoryItem properties)
        {
            return properties is RepositoryItemMRUEdit
                   || (properties is RepositoryItemTextEdit && !(properties is RepositoryItemPopupBase));
        }

        // An empty text clears the cell only where the application said the editor takes null.
        private static bool AcceptsNull(RepositoryItem properties)
        {
            var text = properties as RepositoryItemTextEdit;
            return text != null && text.AllowNullInput == DefaultBoolean.True;
        }

        private static bool Refuse(GridEditRejection why, string because, out GridEditRejection reason, out string message)
        {
            reason = why;
            message = because;
            return false;
        }

        private static bool TryParseIsoDate(string text, out DateTime date)
        {
            if (!DateTime.TryParseExact(text.Trim(), IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date))
                return false;
            if (date.Kind == DateTimeKind.Utc) date = date.ToLocalTime();
            return true;
        }

        // "PT1H30M", "P1DT2H", "-PT5S": the duration forms of ISO 8601 that System.Xml understands.
        private static bool TryParseIsoDuration(string text, out TimeSpan span)
        {
            span = TimeSpan.Zero;
            string trimmed = text.Trim().ToUpperInvariant();
            if (!trimmed.StartsWith("P", StringComparison.Ordinal) && !trimmed.StartsWith("-P", StringComparison.Ordinal))
                return false;
            try
            {
                span = XmlConvert.ToTimeSpan(trimmed);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        /// <summary>
        /// GetKeyValueByDisplayText answers with the first match, so a second row with the same display text has to be
        /// looked for in the list the lookup was given. When the rows cannot be walked (no value member, an unusual
        /// source) no ambiguity can be seen and the first match stands.
        /// </summary>
        private static bool HasAnotherLookUpMatch(RepositoryItemLookUpEdit lookup, string text)
        {
            object list = ListBindingHelper.GetList(lookup.DataSource);
            var rows = list as IEnumerable;
            if (rows == null) return false;
            PropertyDescriptor valueProperty = null;
            if (!string.IsNullOrEmpty(lookup.ValueMember))
            {
                valueProperty = ListBindingHelper.GetListItemProperties(list).Find(lookup.ValueMember, true);
                if (valueProperty == null) return false;
            }

            int matches = 0;
            foreach (object row in rows)
            {
                object key = valueProperty == null ? row : valueProperty.GetValue(row);
                if (string.Equals(lookup.GetDisplayText(key), text, StringComparison.Ordinal) && ++matches > 1) return true;
            }
            return false;
        }
    }
}
#endif

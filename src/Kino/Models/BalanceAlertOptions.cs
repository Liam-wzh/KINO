using System.Globalization;

namespace Kino.Models
{
    public sealed class BalanceAlertOptions
    {
        public bool Enabled { get; set; }
        public decimal ThresholdUsd { get; set; } = 5m;
        public int CooldownMinutes { get; set; } = 60;
        public BalanceAlertOptions Clone() => (BalanceAlertOptions)MemberwiseClone();
        public string Validate()
        {
            if (ThresholdUsd < 0) return "提醒阈值不能小于 0 USD。";
            if (CooldownMinutes < 1 || CooldownMinutes > 10080) return "提醒间隔请填写 1–10080 分钟。";
            return null;
        }
        public void Normalize()
        {
            // A malformed setting must not silently enable a balance alert.
            if (Validate() == null) return;
            Enabled = false; ThresholdUsd = 5m; CooldownMinutes = 60;
        }
        public static BalanceAlertOptions FromInput(bool enabled, string threshold, string cooldown)
        {
            if (!decimal.TryParse(threshold, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var amount))
                throw new ArgumentException("请填写有效的 USD 提醒阈值，小数使用英文句点。");
            if (!int.TryParse(cooldown, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                throw new ArgumentException("提醒间隔请填写 1–10080 分钟。");
            var options = new BalanceAlertOptions { Enabled = enabled, ThresholdUsd = amount, CooldownMinutes = minutes };
            var error = options.Validate(); if (error != null) throw new ArgumentException(error); return options;
        }
    }
}

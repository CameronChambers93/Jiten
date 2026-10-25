namespace Jiten.Core.Data;

/// <summary>A site setting edited from the admin dashboard, stored as JSON under a key.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}

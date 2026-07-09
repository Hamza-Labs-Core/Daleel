namespace Daleel.Core.Models;

/// <summary>
/// Shared SKU handling for product identity. A "strong" SKU is a GLOBAL product id (GTIN / UPC / EAN /
/// MPN) — not a store-internal code — so two listings that carry the same one are provably the same
/// product and can be merged across stores. Store-internal "sku" codes are deliberately NOT used here
/// (they collide across unrelated products). Kept in one place so every identity encoding recognises and
/// normalizes SKUs identically.
/// </summary>
public static class ProductIdentity
{
    /// <summary>Uppercase, alphanumeric-only form of a SKU (strips spaces/dashes) — the canonical compare key.</summary>
    public static string NormalizeSku(string? sku) =>
        string.IsNullOrWhiteSpace(sku)
            ? string.Empty
            : new string(sku.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>
    /// True when a SKU is usable as an identity key: a global id is at least 6 alphanumerics (UPC-A is 12,
    /// EAN-13 is 13, MPNs vary but are rarely shorter), which filters junk/placeholder codes that would
    /// wrongly merge unrelated products.
    /// </summary>
    public static bool HasStrongSku(string? sku) => NormalizeSku(sku).Length >= 6;
}

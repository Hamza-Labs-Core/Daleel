using System.Net;
using System.Text;
using Microsoft.Extensions.Localization;
using Daleel.Web.Resources;

namespace Daleel.Web.Email;

/// <summary>The rendered pieces of an email: a localized subject and an HTML body (with a text fallback).</summary>
public sealed record EmailContent(string Subject, string HtmlBody, string TextBody);

/// <summary>
/// Renders the "your search results are ready" email as table-based, fully-inlined HTML that survives the
/// quirks of real email clients (Gmail/Outlook strip <c>&lt;style&gt;</c> blocks, so every rule is an inline
/// <c>style=</c> attribute, and layout uses tables, not flexbox/grid). All copy comes through the
/// <see cref="SharedResource"/> localizer, so the caller selecting the culture selects the language.
/// </summary>
public sealed class SearchResultEmailTemplate
{
    // Daleel branding (light palette — emails render on a white canvas).
    private const string Brand = "#2f6df6";       // signal blue (primary)
    private const string HeaderBg = "#0d1220";    // near-black navy (app bar)
    private const string PageBg = "#f6f8fc";
    private const string CardBg = "#ffffff";
    private const string Border = "#e3e8f2";
    private const string TextMain = "#11182a";
    private const string TextMuted = "#6b7689";

    private readonly IStringLocalizer<SharedResource> _l;

    public SearchResultEmailTemplate(IStringLocalizer<SharedResource> l) => _l = l;

    public EmailContent Render(SearchResultEmailModel model)
    {
        var dir = model.IsRtl ? "rtl" : "ltr";
        var align = model.IsRtl ? "right" : "left";
        var sb = new StringBuilder(4096);

        sb.Append("<!DOCTYPE html><html lang=\"").Append(Enc(model.Language)).Append("\" dir=\"").Append(dir)
          .Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("</head>");

        // Body wrapper. The hidden preheader controls the inbox preview snippet.
        sb.Append("<body dir=\"").Append(dir).Append("\" style=\"margin:0;padding:0;background:").Append(PageBg)
          .Append(";\">");
        sb.Append("<span style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">")
          .Append(L("Email.Search.Preheader")).Append("</span>");

        // Full-width background table → centered 600px container.
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:")
          .Append(PageBg).Append(";\"><tr><td align=\"center\" style=\"padding:24px 12px;\">");
        sb.Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" dir=\"").Append(dir)
          .Append("\" style=\"width:600px;max-width:600px;background:").Append(CardBg)
          .Append(";border:1px solid ").Append(Border)
          .Append(";border-radius:14px;overflow:hidden;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;\">");

        AppendHeader(sb);
        AppendBody(sb, model, align);
        AppendFooter(sb, align);

        sb.Append("</table></td></tr></table></body></html>");

        return new EmailContent(L("Email.Search.Subject"), sb.ToString(), BuildText(model));
    }

    private void AppendHeader(StringBuilder sb)
    {
        // Brand bar: navy background, white wordmark with a blue accent dot, matching the in-app header.
        sb.Append("<tr><td style=\"background:").Append(HeaderBg).Append(";padding:22px 28px;\">")
          .Append("<span style=\"font-size:20px;font-weight:700;color:#ffffff;letter-spacing:.2px;\">دليل</span>")
          .Append("<span style=\"font-size:20px;font-weight:600;color:").Append(Brand).Append(";\">&nbsp;·&nbsp;</span>")
          .Append("<span style=\"font-size:20px;font-weight:700;color:#ffffff;\">Daleel</span>")
          .Append("</td></tr>");
    }

    private void AppendBody(StringBuilder sb, SearchResultEmailModel model, string align)
    {
        sb.Append("<tr><td style=\"padding:28px;text-align:").Append(align).Append(";\">");

        // Heading + the query echoed back as the focal line.
        sb.Append("<h1 style=\"margin:0 0 6px;font-size:22px;line-height:1.3;color:").Append(TextMain)
          .Append(";\">").Append(L("Email.Search.Heading")).Append("</h1>");
        sb.Append("<p style=\"margin:0 0 18px;font-size:14px;color:").Append(TextMuted).Append(";\">")
          .Append(L("Email.Search.Intro")).Append("</p>");
        sb.Append("<div style=\"font-size:18px;font-weight:600;color:").Append(Brand)
          .Append(";background:#eef3ff;border:1px solid ").Append(Border)
          .Append(";border-radius:10px;padding:12px 16px;margin:0 0 24px;\">")
          .Append(Enc(model.Query)).Append("</div>");

        AppendSummary(sb, model);
        AppendProducts(sb, model, align);
        AppendStores(sb, model, align);
        AppendCta(sb, model);

        sb.Append("</td></tr>");
    }

    private void AppendSummary(StringBuilder sb, SearchResultEmailModel model)
    {
        // Three equal stat cells: big number + label. Table cells (not flex) so it holds in every client.
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 8px;\"><tr>");
        AppendStat(sb, model.ProductCount, L("Email.Search.Products"));
        AppendStat(sb, model.BrandCount, L("Email.Search.Brands"));
        AppendStat(sb, model.StoreCount, L("Email.Search.Stores"));
        sb.Append("</tr></table>");
    }

    private void AppendStat(StringBuilder sb, int count, string label)
    {
        sb.Append("<td width=\"33%\" align=\"center\" style=\"padding:12px 6px;background:#f7f9fd;border:1px solid ")
          .Append(Border).Append(";border-radius:10px;\">")
          .Append("<div style=\"font-size:26px;font-weight:700;color:").Append(TextMain).Append(";\">")
          .Append(count).Append("</div>")
          .Append("<div style=\"font-size:12px;color:").Append(TextMuted).Append(";text-transform:uppercase;letter-spacing:.4px;\">")
          .Append(label).Append("</div></td>");
        sb.Append("<td width=\"8\" style=\"width:8px;\"></td>");
    }

    private void AppendProducts(StringBuilder sb, SearchResultEmailModel model, string align)
    {
        if (model.TopProducts.Count == 0)
        {
            return;
        }

        AppendSectionTitle(sb, L("Email.Search.TopProducts"));
        foreach (var p in model.TopProducts)
        {
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 10px;border:1px solid ")
              .Append(Border).Append(";border-radius:10px;\"><tr>");

            // Thumbnail (fixed box; falls back to a tinted placeholder when no image).
            sb.Append("<td width=\"72\" style=\"width:72px;padding:10px;\">");
            if (!string.IsNullOrWhiteSpace(p.ImageUrl))
            {
                sb.Append("<img src=\"").Append(Enc(p.ImageUrl)).Append("\" width=\"56\" height=\"56\" alt=\"\" ")
                  .Append("style=\"width:56px;height:56px;object-fit:cover;border-radius:8px;display:block;\">");
            }
            else
            {
                sb.Append("<div style=\"width:56px;height:56px;border-radius:8px;background:#eef3ff;\"></div>");
            }
            sb.Append("</td>");

            sb.Append("<td style=\"padding:10px 12px;text-align:").Append(align).Append(";\">")
              .Append("<div style=\"font-size:15px;font-weight:600;color:").Append(TextMain).Append(";\">")
              .Append(Enc(p.Name)).Append("</div>")
              .Append("<div style=\"font-size:13px;color:").Append(Brand).Append(";margin-top:3px;\">")
              .Append(string.IsNullOrWhiteSpace(p.PriceRange) ? L("Email.Search.PriceNa") : Enc(p.PriceRange!))
              .Append("</div></td></tr></table>");
        }
    }

    private void AppendStores(StringBuilder sb, SearchResultEmailModel model, string align)
    {
        if (model.TopStores.Count == 0)
        {
            return;
        }

        AppendSectionTitle(sb, L("Email.Search.TopStores"));
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid ")
          .Append(Border).Append(";border-radius:10px;\">");
        foreach (var s in model.TopStores)
        {
            sb.Append("<tr><td style=\"padding:11px 14px;border-bottom:1px solid ").Append(Border)
              .Append(";text-align:").Append(align).Append(";\">")
              .Append("<span style=\"font-size:14px;font-weight:600;color:").Append(TextMain).Append(";\">")
              .Append(Enc(s.Name)).Append("</span>");
            if (!string.IsNullOrWhiteSpace(s.Location))
            {
                sb.Append("<span style=\"font-size:13px;color:").Append(TextMuted).Append(";\"> — ")
                  .Append(Enc(s.Location!)).Append("</span>");
            }
            sb.Append("</td></tr>");
        }
        sb.Append("</table>");
    }

    private void AppendCta(StringBuilder sb, SearchResultEmailModel model)
    {
        // Bulletproof, centered button: a styled <a> inside a table cell.
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:26px 0 4px;\"><tr><td align=\"center\">")
          .Append("<a href=\"").Append(Enc(model.CtaUrl))
          .Append("\" style=\"display:inline-block;background:").Append(Brand)
          .Append(";color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;padding:13px 32px;border-radius:10px;\">")
          .Append(L("Email.Search.ViewReport")).Append("</a></td></tr></table>");
    }

    private void AppendFooter(StringBuilder sb, string align)
    {
        sb.Append("<tr><td style=\"padding:20px 28px;border-top:1px solid ").Append(Border)
          .Append(";background:#fafbfe;text-align:").Append(align).Append(";\">")
          .Append("<p style=\"margin:0 0 4px;font-size:12px;color:").Append(TextMuted).Append(";\">")
          .Append(L("Email.Search.FooterReason")).Append("</p>")
          .Append("<p style=\"margin:0;font-size:12px;color:").Append(TextMuted).Append(";\">")
          .Append(L("Email.Search.FooterManage")).Append("</p></td></tr>");
    }

    private void AppendSectionTitle(StringBuilder sb, string title) =>
        sb.Append("<div style=\"font-size:13px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;color:")
          .Append(TextMuted).Append(";margin:22px 0 10px;\">").Append(title).Append("</div>");

    private string BuildText(SearchResultEmailModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L("Email.Search.Heading"));
        sb.AppendLine(L("Email.Search.Intro"));
        sb.AppendLine(model.Query);
        sb.AppendLine();
        sb.Append(model.ProductCount).Append(' ').Append(L("Email.Search.Products")).Append(" · ")
          .Append(model.BrandCount).Append(' ').Append(L("Email.Search.Brands")).Append(" · ")
          .Append(model.StoreCount).Append(' ').AppendLine(L("Email.Search.Stores"));
        sb.AppendLine();
        sb.AppendLine(L("Email.Search.ViewReport") + ": " + model.CtaUrl);
        return sb.ToString();
    }

    /// <summary>Localizes a key under the ambient culture (the caller sets it to the user's language).</summary>
    private string L(string key) => _l[key];

    /// <summary>HTML-encodes dynamic text/URLs so a hostile product name or query can't inject markup.</summary>
    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

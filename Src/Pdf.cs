﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using RT.Json;
using RT.Servers;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace KtaneWeb
{
    public sealed partial class KtanePropellerModule
    {
        private HttpResponse pdfOrFileSystem(HttpRequest req)
        {
            if (!req.Url.Path.StartsWith("/PDF/", StringComparison.InvariantCultureIgnoreCase))
                goto doFileSystem;

            var filename = req.Url.Path.Substring(5);
            if (filename.Length < 1 || filename.Contains('/'))
                goto doFileSystem;
            filename = filename.UrlUnescape();

            // If the PDF file already exists in the PDF folder, use that
            if (File.Exists(Path.Combine(_config.BaseDir, "PDF", filename)))
                goto doFileSystem;

            // See if an equivalent HTML file exists, even with a wildcard match or incorrect filename capitilization
            string htmlFile = new DirectoryInfo(Path.Combine(_config.BaseDir, "HTML")).GetFiles(Path.GetFileNameWithoutExtension(filename) + ".html").Select(fs => fs.FullName).FirstOrDefault();
            if (htmlFile == null)
                goto doFileSystem;

            // Check if the PDF filename is exactly correct and redirect if it isn’t
            string pdfUrl = $"/PDF/{Path.GetFileNameWithoutExtension(htmlFile)}.pdf";
            if (!Regex.IsMatch(pdfUrl, $"^{Regex.Escape("/PDF/" + filename).Replace("\\*", ".*")}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                goto doFileSystem;
            if (pdfUrl != req.Url.Path.Substring(0, 5) + filename)
                return HttpResponse.Redirect(req.Url.WithPath(pdfUrl));

            // Turns out an HTML file corresponding to the requested PDF file exists, so we will try to generate the PDF automatically by invoking Google Chrome
            KtaneModuleInfo module = null;
            try
            {
                module = _moduleInfoCache.Modules.First(mod => mod.FileName == Path.GetFileNameWithoutExtension(filename));
            }
            catch { }
            return HttpResponse.File(generatePdf(htmlFile, false, module?.PageRenderTime ?? defaultRenderDelay).filename, "application/pdf");

            doFileSystem:
            return new FileSystemHandler(_config.BaseDir).Handle(req);
        }

        private const int defaultRenderDelay = 500;
        /// <summary>
        ///     If <paramref name="avoidGenerating"/> is <c>false</c>, generates a PDF for the specified HTML file. If
        ///     <paramref name="avoidGenerating"/> is <c>true</c>, examines whether a PDF would need to be generated.</summary>
        /// <param name="htmlFile">
        ///     Full path to the HTML file.</param>
        /// <param name="avoidGenerating">
        ///     If <c>true</c>, no PDFs are generated.</param>
        /// <returns>
        ///     The filename of the generated (or would-be generated) PDF, and a boolean specifying whether the PDF was
        ///     generated/would be generated (<c>true</c>) or was already available (<c>false</c>).</returns>
        private (string filename, bool wasGenerated) generatePdf(string htmlFile, bool avoidGenerating = false, int renderDelay = defaultRenderDelay)
        {
            var cache = _moduleInfoCache;
            lock (cache.AutogeneratedPdfs)
            {
                if (cache.AutogeneratedPdfs.TryGetValue(htmlFile, out var pdfFile) && File.Exists(pdfFile))
                    return (pdfFile, false);

                using var md5 = MD5.Create();
                var tempFilename = $"{md5.ComputeHash(File.ReadAllBytes(htmlFile)).ToHex()}.pdf";
                var tempFilepath = Path.Combine(_config.PdfTempPath ?? Path.GetTempPath(), tempFilename);
                var didGenerate = false;
                if (!File.Exists(tempFilepath))
                {
                    if (avoidGenerating)
                        return (tempFilepath, true);
                    var runner = new CommandRunner();
                    // Weird workaround: Chrome does not appear to accept apostrophes in filename, but it’s ok with %27
                    var htmlFileEscaped = htmlFile.Replace("'", "%27");
                    runner.Command = $@"cmd.exe /S /C """"{_config.ChromePath}"" --headless --disable-gpu ""--print-to-pdf={tempFilepath}"" ""--virtual-time-budget={renderDelay}"" --run-all-compositor-stages-before-draw --no-margins ""{htmlFileEscaped}""""";
                    runner.StartAndWait();
                    didGenerate = true;
                }
                else
                    File.SetLastAccessTimeUtc(tempFilepath, DateTime.UtcNow);

                cache.AutogeneratedPdfs[htmlFile] = tempFilepath;
                return (tempFilepath, didGenerate);
            }
        }

        private HttpResponse pdfDiag(HttpRequest req)
        {
            var cache = _moduleInfoCache;
            var availableOriginals = 0;
            var availableAll = 0;
            var countOriginals = 0;
            var countAll = 0;
            return HttpResponse.Html(new HTML(
                new HEAD(
                    new TITLE("PDF diagnostics"),
                    new STYLELiteral("" +
                    "body { font-size: 12pt; }" +
                    "table { border: 2px solid black; border-collapse: collapse; }" +
                    "td, th { border: 1px solid black; text-align: left; vertical-align: top; padding: .1cm .5cm; }" +
                    "th { font-size: 18pt; }" +
                    "td.html-missing { background: #fee; }" +
                    "td.pdf-missing { background: #ffe; }" +
                    "td.exists { background: #efe; }")),
                new BODY(
                    new TABLE(cache.ModulesJson["KtaneModules"].GetList().OrderBy(m => m["Name"].GetString(), StringComparer.OrdinalIgnoreCase).Select(module =>
                      {
                          var sheetslist = module["Sheets"].GetList().Select(jv => jv.GetString()).Where(sh => sh.Contains("|pdf|")).ToArray();
                          countAll += sheetslist.Length;
                          countOriginals++;
                          return sheetslist.Select((sheet, ix) =>
                          {
                              var moduleFilename = module.ContainsKey("FileName") ? module["FileName"].GetString() : module["Name"].GetString();
                              var pdfFilename = Path.Combine(_config.BaseDir, "PDF", moduleFilename + sheet.Substring(0, sheet.IndexOf('|')) + ".pdf");
                              var cssClass = File.Exists(pdfFilename) ? "exists" : "pdf-missing";
                              var md5hash = "(pdf file)";

                              var htmlFile = Path.Combine(_config.BaseDir, "HTML", moduleFilename + sheet.Substring(0, sheet.IndexOf('|')) + ".html");
                              if (File.Exists(htmlFile))
                              {
                                  using var md5 = MD5.Create();
                                  md5hash = md5.ComputeHash(File.ReadAllBytes(htmlFile)).ToHex();
                                  var tempFilepath = Path.Combine(_config.PdfTempPath ?? Path.GetTempPath(), $"{md5hash}.pdf");
                                  cssClass = File.Exists(tempFilepath) ? "exists" : "pdf-missing";
                              }
                              if (cssClass == "exists")
                              {
                                  availableAll++;
                                  if (sheet.IndexOf('|') == 0)
                                      availableOriginals++;
                              }
                              return new TR(
                                  ix == 0 ? new TH { rowspan = sheetslist.Length }._(module["Name"].GetString()) : null,
                                  new TD { class_ = cssClass }._(sheet.Substring(0, sheet.IndexOf('|'))),
                                  new TD { class_ = cssClass }._(md5hash ?? "-"));
                          });
                      })),
                    new DIV(new Func<object>(() => $"Available: {availableOriginals}/{countOriginals} ({availableOriginals * 100 / (double) countOriginals:0.0}%) originals; {availableAll}/{countAll} ({availableAll * 100 / (double) countAll:0.0}%) total")))));
        }

        private HttpResponse mergePdfs(HttpRequest req)
        {
            string lastExaminedPdfFile = "<none>";
            try
            {
                if (req.Method != HttpMethod.Post)
                    return HttpResponse.Redirect(req.Url.WithPathParent().WithPath(""));

                var messages = new StringBuilder();
                var json = JsonValue.Parse(req.Post["json"].Value);
                json.AppendIndented(messages);
                var keywords = json["search"].GetString().Length == 0 ? null : json["search"].GetString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var searchOptions = json["searchOptions"].GetList().Select(j => j.GetString()).ToArray();
                var filterEnabledByProfile = json["filterEnabledByProfile"].GetBool();
                var filterVetoedByProfile = json["filterVetoedByProfile"].GetBool();
                var profileVetoList = (filterEnabledByProfile == filterVetoedByProfile) ? null : json["profileVetoList"]?.GetList().Select(j => j.GetString()).ToArray();
                var searchBySymbol = json["searchBySymbol"].GetBoolSafe() ?? false;
                var searchBySteamID = json["searchBySteamID"].GetBoolSafe() ?? false;
                var searchByModuleID = json["searchByModuleID"].GetBoolSafe() ?? false;
                var displayAllContributors = json["dispAllContr"].GetBoolSafe() ?? false;
                var displayDesc = json["displayDesc"].GetBoolSafe() ?? false;
                var displayTags = json["displayTags"].GetBoolSafe() ?? false;

                static string unifyString(string str) => Regex.Replace(str.Normalize(NormalizationForm.FormD), @"[\u0300-\u036f]", "").Replace("grey", "gray").Replace("colour", "color");

                // Filter
                var matchingModules = _moduleInfoCache.Modules.Where(m =>
                {
                    // TEMPORARY: Currently there is no easy way to find the correct filename for the manual of a translated module, so we’re excluding those from the merged PDF entirely.
                    // A desirable fix would be to discover the correct PDF filename for the translated manual and include it.
                    if (m.TranslationOf != null)
                        return false;

                    if (profileVetoList != null && !(profileVetoList.Contains(m.ModuleID) ? filterVetoedByProfile : filterEnabledByProfile))
                        return false;

                    foreach (var filter in TranslationInfo.Default.Filters1)
                        if (!filter.Matches(m, json["filter"].Safe[filter.PropName].GetDictSafe()))
                            return false;
                    foreach (var filter in TranslationInfo.Default.Filters2)
                        if (!filter.Matches(m, json["filter"].Safe[filter.PropName].GetDictSafe()))
                            return false;

                    if (keywords == null)
                        return true;

                    var searchWhat = searchBySteamID ? (m.SteamID ?? "") : "";
                    if (searchByModuleID)
                        searchWhat += " " + m.ModuleID.ToLowerInvariant();
                    if (searchOptions.Contains("names"))
                        searchWhat += " " + m.Name.ToLowerInvariant() + " " + m.SortKey.ToLowerInvariant();
                    if (searchOptions.Contains("authors") && (m.Author != null || m.Contributors != null))
                        if (displayAllContributors)
                            searchWhat += " " + (m.Author ?? m.Contributors.ToAllAuthorString()).ToLowerInvariant();
                        else
                            searchWhat += " " + (m.Author ?? m.Contributors.ToAuthorString()).ToLowerInvariant();
                    if (searchOptions.Contains("descriptions"))
                    {
                        var descSplit = m.Description.Split("Tags:");
                        var descriptionOnly = descSplit[0];
                        var descTags = "";
                        if (descSplit.Length > 1)
                            descTags = "Tags:" + descSplit[1];

                        if (!displayTags && displayDesc)
                            searchWhat += ' ' + descriptionOnly.ToLowerInvariant();
                        else if (displayTags && !displayDesc && descSplit.Length > 1)
                            searchWhat += ' ' + descTags.ToLowerInvariant();
                        else
                            searchWhat += ' ' + m.Description.ToLowerInvariant();
                    }
                    if (searchBySymbol && m.Symbol != null)
                        searchWhat += " " + m.Symbol.ToLowerInvariant();

                    return keywords.All(unifyString(searchWhat).ContainsIgnoreCase);
                });

                // Sort
                switch (json["sort"].GetString())
                {
                    case "name": matchingModules = matchingModules.OrderBy(m => m.SortKey); break;
                    case "defdiff": matchingModules = matchingModules.OrderBy(m => m.DefuserDifficulty); break;
                    case "expdiff": matchingModules = matchingModules.OrderBy(m => m.ExpertDifficulty); break;
                    case "twitchscore": matchingModules = matchingModules.OrderBy(m => m.TwitchPlaysScore ?? 0); break;
                    case "timemodescore": matchingModules = matchingModules.OrderBy(m => m.TimeMode?.Score ?? 0); break;
                    case "published": matchingModules = matchingModules.OrderByDescending(m => m.Published); break;
                }

                var pdfFiles = new List<string>();
                var generated = 0;
                var notGenerated = new List<string>();
                var startTime = DateTime.UtcNow;

                foreach (var module in matchingModules)
                {
                    var filename = $"{module.FileName}.pdf";
                    lastExaminedPdfFile = filename;
                    string fullPath = null;
                    if (json["preferredManuals"].ContainsKey(module.Name))
                    {
                        var pref = json["preferredManuals"][module.Name].GetString();
                        var match = Regex.Match(pref, @"^(.*) \((PDF|HTML)\)$");
                        string path;

                        // PDF file exists
                        if (match.Success && match.Groups[2].Value == "PDF" && File.Exists(path = Path.Combine(_config.BaseDir, "PDF", $"{match.Groups[1].Value.Replace(module.Name, module.FileName)}.pdf")))
                        {
                            messages.AppendLine($"{pref} (pref) ⇒ {path}");
                            fullPath = path;
                        }
                        // HTML file exists, regardless if HTML or PDF is selected
                        else if (match.Success && File.Exists(path = Path.Combine(_config.BaseDir, "HTML", $"{match.Groups[1].Value.Replace(module.Name, module.FileName)}.html")))
                        {
                            messages.AppendLine($"{pref} (pref) ⇒ {path}");
                            fullPath = path;
                        }
                    }

                    if (fullPath == null)
                    {
                        fullPath = Path.Combine(_config.BaseDir, _config.PdfDir, filename);
                        if (!File.Exists(fullPath))
                            fullPath = Path.Combine(_config.BaseDir, "HTML", $"{module.FileName}.html");
                        if (!File.Exists(fullPath))
                            return HttpResponse.PlainText($"Cannot find {filename}.", HttpStatusCode._500_InternalServerError);
                        messages.AppendLine($"{module.Name} (no pref) ⇒ {fullPath}");
                    }

                    // Generate PDFs
                    if (fullPath.EndsWith(".html") && File.Exists(fullPath))
                    {
                        var avoidGenerating = (DateTime.UtcNow - startTime).TotalSeconds > 5;
                        var (pdfFilename, pdfGenerated) = generatePdf(fullPath, avoidGenerating, module.PageRenderTime ?? defaultRenderDelay);
                        messages.AppendLine($"{module.Name} avoid={avoidGenerating} gen={pdfGenerated} file={pdfFilename}");
                        if (avoidGenerating && pdfGenerated)
                        {
                            notGenerated.Add(Path.GetFileNameWithoutExtension(fullPath));
                            messages.AppendLine($"{module.Name} added to not-generated list");
                        }
                        else if (!avoidGenerating)
                        {
                            if (pdfGenerated)
                                generated++;
                            fullPath = pdfFilename;
                            messages.AppendLine($"{module.Name} (no pref) ⇒ {fullPath} ({(pdfGenerated ? "generated" : "from cache")})");
                        }
                    }

                    if (File.Exists(fullPath))
                        pdfFiles.Add(fullPath);
                    else
                        messages.AppendLine($" — {fullPath} does not exist");
                }

                if (pdfFiles.Count == 0)
                    return HttpResponse.PlainText($"Error: no matching manuals found.\n\n{messages}", HttpStatusCode._500_InternalServerError);

                if (notGenerated.Count > 0)
                    return HttpResponse.Html($"Looks like you’re asking me to generate a lot of PDF files from HTML. I’ve just generated {generated} and I will need to generate the following {notGenerated.Count} more. Please refresh this page once every minute to incrementally have your PDFs generated. Please do not overload the server with excessive requests for merged PDFs, or this feature will need to be disabled.<ul>{notGenerated.Select(g => $"<li>{g.HtmlEscape()}</li>").JoinString()}</ul><!--\n\n{messages}\n\n-->", HttpStatusCode._202_Accepted);

                var list = pdfFiles.JoinString("\n");
                using var mem = new MemoryStream(list.ToUtf8());
                using var sha1hash = SHA1.Create();
                var sha1 = sha1hash.ComputeHash(mem).ToHex();
                var pdfPath = Path.Combine(_config.BaseDir, _config.MergedPdfsDir, $"{sha1}.pdf");
                if (!File.Exists(pdfPath))
                    lock (this)
                        if (!File.Exists(pdfPath))
                        {
                            var mergedPdf = new PdfDocument();
                            foreach (var pdfFile in pdfFiles)
                            {
                                lastExaminedPdfFile = pdfFile;
                                var pdf = PdfReader.Open(Path.Combine(_config.BaseDir, _config.PdfDir, pdfFile), PdfDocumentOpenMode.Import);
                                int count = pdf.PageCount;
                                for (int idx = 0; idx < count; idx++)
                                    mergedPdf.AddPage(pdf.Pages[idx]);
                            }
                            using var f = File.OpenWrite(pdfPath);
                            mergedPdf.Save(f);
                        }
                return HttpResponse.Redirect(req.Url.WithPathParent().WithPathOnly($"/MergedPdfs/{sha1}.pdf"));
            }
            catch (Exception e)
            {
                var exc = e;
                var sb = new StringBuilder();
                while (exc != null)
                {
                    sb.AppendLine($"Error processing PDFs:\r\n{e.GetType().FullName}\r\n{e.Message}\r\nPossible culprit: {lastExaminedPdfFile}\r\n\r\n{e.StackTrace}\r\n\r\n");
                    exc = exc.InnerException;
                }
                return HttpResponse.PlainText(sb.ToString(), HttpStatusCode._500_InternalServerError);
            }
        }
    }
}

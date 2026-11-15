using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using Xunit;

using A = DocumentFormat.OpenXml.Drawing;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace AIB.Tests
{
    /// <summary>
    /// Editar .xlsm sem perder macro, botão nem nome interno de aba. Pedido do usuário
    /// (15/11/2026). Medido numa cópia do inventário de TI dele: o ClosedXML 0.105 manteve o
    /// vbaProject.bin, mas apagou o botão "SALVAR" (ligado a Módulo1.CadastrarItem) e o codeName
    /// das seis abas. Antes, a doc recusava qualquer arquivo com macro.
    /// </summary>
    public class MacrosDePlanilhaTests : IDisposable
    {
        private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-xlsm-" + Guid.NewGuid().ToString("N"));
        private static readonly byte[] Vba = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 % 251)).ToArray();

        public MacrosDePlanilhaTests() => Directory.CreateDirectory(_pasta);

        public void Dispose()
        {
            try { Directory.Delete(_pasta, true); } catch { }
        }

        private static JsonElement Json(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Como o inventário: abas "Painel" e "Cadastro" com codeName, macro, e um botão
        /// "SALVAR" desenhado no Cadastro que roda Módulo1.Salvar.
        /// </summary>
        private string Inventario(string nome = "inventario.xlsm", bool assinada = false, bool botao = true, bool macro = true)
        {
            string c = Path.Combine(_pasta, nome);
            using (var wb = new XLWorkbook())
            {
                wb.AddWorksheet("Painel").Cell("A1").Value = "Total";
                var cad = wb.AddWorksheet("Cadastro");
                cad.Cell("A1").Value = "Item";
                cad.Cell("B1").Value = "Qtd";
                cad.Cell("A2").Value = "Mouse";
                cad.Cell("B2").Value = 3;
                wb.SaveAs(Path.ChangeExtension(c, ".xlsx"));
            }
            File.Move(Path.ChangeExtension(c, ".xlsx"), c, true);

            // O tipo do .xlsm direto no [Content_Types].xml, como o Excel grava: o
            // ChangeDocumentType do SDK renomeia a parte principal e tira o xl/workbook.xml.
            if (macro)
            {
                using var zip = System.IO.Compression.ZipFile.Open(c, System.IO.Compression.ZipArchiveMode.Update);
                var tipos = zip.GetEntry("[Content_Types].xml")!;
                string xml;
                using (var r = new StreamReader(tipos.Open())) xml = r.ReadToEnd();
                tipos.Delete();
                using var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
                w.Write(xml.Replace("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                                    "application/vnd.ms-excel.sheet.macroEnabled.main+xml"));
            }

            using (var doc = SpreadsheetDocument.Open(c, true))
            {
                if (macro)
                {
                    var vba = doc.WorkbookPart!.AddNewPart<VbaProjectPart>();
                    using (var s = new MemoryStream(Vba)) vba.FeedData(s);
                }

                int n = 1;
                foreach (var sheet in doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>())
                {
                    var ws = (WorksheetPart)doc.WorkbookPart.GetPartById(sheet.Id!);
                    var props = ws.Worksheet.SheetProperties ?? ws.Worksheet.InsertAt(new SheetProperties(), 0);
                    props.CodeName = "Planilha" + n++;

                    if (botao && sheet.Name == "Cadastro")
                    {
                        var desenho = ws.AddNewPart<DrawingsPart>();
                        desenho.WorksheetDrawing = new Xdr.WorksheetDrawing(
                            new Xdr.TwoCellAnchor(
                                new Xdr.FromMarker(new Xdr.ColumnId("1"), new Xdr.ColumnOffset("0"), new Xdr.RowId("5"), new Xdr.RowOffset("0")),
                                new Xdr.ToMarker(new Xdr.ColumnId("3"), new Xdr.ColumnOffset("0"), new Xdr.RowId("7"), new Xdr.RowOffset("0")),
                                new Xdr.Shape(
                                    new Xdr.NonVisualShapeProperties(
                                        new Xdr.NonVisualDrawingProperties { Id = 2U, Name = "Retângulo 1" },
                                        new Xdr.NonVisualShapeDrawingProperties()),
                                    new Xdr.ShapeProperties(new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
                                    new Xdr.TextBody(new A.BodyProperties(), new A.ListStyle(),
                                        new A.Paragraph(new A.Run(new A.Text("SALVAR")))))
                                { Macro = "[0]!Módulo1.Salvar", TextLink = "" },
                                new Xdr.ClientData()));
                        ws.Worksheet.Append(new Drawing { Id = ws.GetIdOfPart(desenho) });
                    }
                    ws.Worksheet.Save();
                }
            }
            // A assinatura é a parte que o Excel grava ao lado do vbaProject.bin.
            if (assinada)
            {
                using var zip = System.IO.Compression.ZipFile.Open(c, System.IO.Compression.ZipArchiveMode.Update);
                using var s = zip.CreateEntry("xl/vbaProjectSignature.bin").Open();
                s.Write(new byte[] { 1, 2, 3 });
            }
            return c;
        }

        private static (byte[]? Vba, string[] CodeNames, string? Botao, string? Macro, bool Habilitado) Conferir(string c, string abaDoBotao = "Cadastro")
        {
            using var doc = SpreadsheetDocument.Open(c, false);
            var wbPart = doc.WorkbookPart!;
            byte[]? vba = null;
            if (wbPart.VbaProjectPart is { } v)
            {
                using var s = v.GetStream();
                using var m = new MemoryStream();
                s.CopyTo(m);
                vba = m.ToArray();
            }

            var nomes = wbPart.Workbook.Sheets!.Elements<Sheet>()
                .Select(s => ((WorksheetPart)wbPart.GetPartById(s.Id!)).Worksheet.SheetProperties?.CodeName?.Value ?? "")
                .ToArray();

            var aba = wbPart.Workbook.Sheets!.Elements<Sheet>().First(s => s.Name == abaDoBotao);
            var ws = (WorksheetPart)wbPart.GetPartById(aba.Id!);
            var forma = ws.DrawingsPart?.WorksheetDrawing?.Descendants<Xdr.Shape>().FirstOrDefault();
            ws.Worksheet.Elements<Drawing>().Count().Should().Be(forma == null ? 0 : 1, "o <drawing> aponta para o desenho");

            return (vba, nomes, forma?.InnerText, forma?.Macro?.Value, doc.DocumentType == SpreadsheetDocumentType.MacroEnabledWorkbook);
        }

        [Fact]
        public void EditarXlsm_MantemMacroBotaoENomesDasAbas()
        {
            string c = Inventario();
            var g = EditorDePlanilha.Planejar(c, Json("""[{"sheet":"Cadastro","cell":"B2","value":5},{"sheet":"Cadastro","range":"A1:B1","style":{"bold":true}}]"""), null);

            g.Plano.Consequencia.Should().Contain("As macros e o botão (ou forma) desenhado ficam como estão; o AIB não executa macros.");
            g.Gravar();

            var r = Conferir(c);
            r.Vba.Should().Equal(Vba, "o código das macros fica byte a byte");
            r.Habilitado.Should().BeTrue("continua um .xlsm com macros");
            r.CodeNames.Should().Equal("Planilha1", "Planilha2");
            r.Botao.Should().Be("SALVAR");
            r.Macro.Should().Be("[0]!Módulo1.Salvar");

            using var wb = new XLWorkbook(c);
            wb.Worksheet("Cadastro").Cell("B2").GetDouble().Should().Be(5);
        }

        [Fact]
        public void AbaRenomeada_LevaOBotaoEONomeInterno()
        {
            string c = Inventario();
            EditorDePlanilha.Planejar(c, Json("""[{"rename_sheet":"Cadastro","to":"Entrada"},{"sheet":"Entrada","cell":"A3","value":"Teclado"}]"""), null).Gravar();

            var r = Conferir(c, "Entrada");
            r.Botao.Should().Be("SALVAR");
            r.CodeNames.Should().Equal("Planilha1", "Planilha2");
        }

        [Fact]
        public void InserirLinhaNaAbaDoBotao_ERecusado_EEmOutraAba_Pode()
        {
            string c = Inventario();

            var naDoBotao = () => EditorDePlanilha.Planejar(c, Json("""[{"sheet":"Cadastro","insert_rows":2,"rows":[["Cabo",1]]}]"""), null);
            naDoBotao.Should().Throw<ErroDeDocumento>().WithMessage("*\"Cadastro\" tem botões ou formas desenhadas*");

            var imagem = Path.Combine(_pasta, "logo.png");
            File.WriteAllBytes(imagem, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
            var comImagem = () => EditorDePlanilha.Planejar(c,
                Json(JsonSerializer.Serialize(new[] { new { sheet = "Cadastro", cell = "E1", image = imagem } })), null);
            comImagem.Should().Throw<ErroDeDocumento>().WithMessage("*botões ou formas*");

            EditorDePlanilha.Planejar(c, Json("""[{"sheet":"Painel","insert_rows":1,"rows":[["Resumo"]]}]"""), null).Gravar();
            Conferir(c).Botao.Should().Be("SALVAR");
        }

        [Fact]
        public void XlsxSemMacro_ComForma_TambemNaoPerdeAForma()
        {
            // Antes da mudança, uma forma num .xlsx comum sumia sem aviso na primeira edição.
            string c = Inventario("formas.xlsx", macro: false);
            EditorDePlanilha.Planejar(c, Json("""[{"sheet":"Painel","cell":"B1","value":10}]"""), null).Gravar();

            var r = Conferir(c);
            r.Botao.Should().Be("SALVAR");
            r.Vba.Should().BeNull();
        }

        [Fact]
        public void MacroAssinada_ContinuaRecusada()
        {
            string c = Inventario("assinada.xlsm", assinada: true);
            var planejar = () => EditorDePlanilha.Planejar(c, Json("""[{"cell":"A1","value":1}]"""), null);
            planejar.Should().Throw<ErroDeDocumento>().WithMessage("*macros assinadas*");
        }

        [Fact]
        public void XlsmSemBotao_SoComMacro_PodeEditar()
        {
            string c = Inventario("so-macro.xlsm", botao: false);
            var g = EditorDePlanilha.Planejar(c, Json("""[{"sheet":"Painel","cell":"B1","value":1}]"""), null);
            g.Plano.Consequencia.Should().Contain("As macros ficam como estão");
            g.Gravar();

            var r = Conferir(c);
            r.Vba.Should().Equal(Vba);
            r.CodeNames.Should().Equal("Planilha1", "Planilha2");
        }
    }
}

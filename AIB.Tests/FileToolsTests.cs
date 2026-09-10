using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Ferramentas de arquivo. Tudo acontece dentro de um diretório temporário próprio —
    /// nenhum teste toca caminho real do usuário.
    /// </summary>
    public class FileToolsTests : IDisposable
    {
        private readonly string _dir;

        public FileToolsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AIB_FileToolsTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Path_(string name) => Path.Combine(_dir, name);

        private static string Args(params (string Key, string Value)[] pairs)
        {
            var dict = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var (k, v) in pairs) dict[k] = v;
            return JsonSerializer.Serialize(dict);
        }

        // ── ReadFileTool ──────────────────────────────────────────────────────

        [Fact]
        public async Task ReadFile_LeOConteudoDeUmArquivoReal()
        {
            string file = Path_("ok.txt");
            await File.WriteAllTextAsync(file, "conteúdo esperado");

            string result = await new ReadFileTool().ExecuteAsync(Args(("path", file)));

            // O conteudo vem com NUMERO DE LINHA. E o que faz a edicao por trecho ancorar e o
            // que transforma "erro na linha 26" em algo que se possa achar; sem isso o modelo
            // conta linhas de cabeca e erra.
            result.Should().Contain("conteúdo esperado");
            result.Should().MatchRegex(@"^\s*1\t");
        }

        [Fact]
        public async Task ReadFile_LeSO_AFaixaPedida()
        {
            // Antes vinha o arquivo inteiro. Numa maquina onde o prefill e o custo dominante,
            // trazer duzentas linhas para ver tres campos e pagar duzentas linhas.
            string file = Path_("longo.txt");
            await File.WriteAllLinesAsync(file, Enumerable.Range(1, 50).Select(i => $"linha {i}"));

            string result = await new ReadFileTool().ExecuteAsync(
                JsonSerializer.Serialize(new { path = file, offset = 10, limit = 3 }));

            result.Should().Contain("linha 10").And.Contain("linha 12");
            result.Should().NotContain("linha 9").And.NotContain("linha 13");
            result.Should().Contain("offset=13", "a resposta diz como continuar");
        }

        [Fact]
        public async Task ReadFile_NumaPASTA_ListaAPasta()
        {
            // Antes respondia "arquivo nao encontrado" — mentira, a pasta existe. Foi o primeiro
            // passo em falso de uma cadeia que custou dois turnos: o modelo perguntou pela
            // pasta, ouviu que nao existia, e foi listar pelo shell o que a ferramenta ja tinha.
            string sub = Path.Combine(_dir, "dentro");
            Directory.CreateDirectory(sub);
            await File.WriteAllTextAsync(Path.Combine(_dir, "users.csv"), "id;nome");

            string result = await new ReadFileTool().ExecuteAsync(Args(("path", _dir)));

            result.Should().Contain("é uma PASTA");
            result.Should().Contain("users.csv");
            result.Should().Contain("dentro\\");
            result.Should().NotContain("ERRO");
        }

        [Fact]
        public async Task ReadFile_SemOParametroPath_Recusa()
        {
            string result = await new ReadFileTool().ExecuteAsync("{}");

            result.Should().StartWith("ERRO");
            result.Should().Contain("path");
        }

        [Fact]
        public async Task ReadFile_CaminhoVazio_Recusa()
        {
            string result = await new ReadFileTool().ExecuteAsync(Args(("path", "   ")));

            result.Should().StartWith("ERRO");
        }

        [Fact]
        public async Task ReadFile_ArquivoInexistente_Recusa()
        {
            string result = await new ReadFileTool().ExecuteAsync(Args(("path", Path_("nao_existe.txt"))));

            result.Should().StartWith("ERRO");
            result.Should().Contain("não existe");

            // E diz o que EXISTE na pasta, que e o que evita a segunda tentativa as cegas.
            result.Should().Contain(_dir);
        }

        [Fact]
        public async Task ReadFile_JsonQuebrado_ViraErroDeTexto_NuncaExcecao()
        {
            var tool = new ReadFileTool();

            Func<Task> act = async () => await tool.ExecuteAsync("{\"path\": ");

            await act.Should().NotThrowAsync();
            (await tool.ExecuteAsync("{\"path\": ")).Should().StartWith("ERRO");
        }

        [Fact]
        public async Task ReadFile_LinhaAbsurdamenteLonga_EhCortada()
        {
            // Um arquivo minificado e uma linha so, de cem mil caracteres. O teto agora e POR
            // LINHA, e nao do arquivo: o corte do arquivo virou faixa, que o modelo controla.
            string file = Path_("grande.txt");
            await File.WriteAllTextAsync(file, new string('x', 20000));

            string result = await new ReadFileTool().ExecuteAsync(Args(("path", file)));

            result.Should().Contain("linha cortada");
            result.Length.Should().BeLessThan(ReadFileTool.TetoDaLinha + 200);
        }

        [Fact]
        public async Task ReadFile_MuitasLinhas_ParaNoTetoEDizComoSeguir()
        {
            string file = Path_("muitas.txt");
            await File.WriteAllLinesAsync(
                file, Enumerable.Range(1, ReadFileTool.LinhasPadrao + 50).Select(i => $"L{i}"));

            string result = await new ReadFileTool().ExecuteAsync(Args(("path", file)));

            result.Should().Contain($"L{ReadFileTool.LinhasPadrao}");
            result.Should().NotContain($"L{ReadFileTool.LinhasPadrao + 1}\n");
            result.Should().Contain($"offset={ReadFileTool.LinhasPadrao + 1}");
        }

        [Fact]
        public void ReadFile_SchemaDeclaraPathObrigatorio()
        {
            var tool = new ReadFileTool();

            using var doc = JsonDocument.Parse(tool.ChatToolDefinition.FunctionParameters.ToString());
            var root = doc.RootElement;

            root.GetProperty("properties").TryGetProperty("path", out _).Should().BeTrue();
            root.GetProperty("required").EnumerateArray()
                .Select(e => e.GetString()).Should().Contain("path");
            tool.Name.Should().Be("read");
            tool.RequiredLevel.Should().Be(1);
        }

        // ── WriteFileTool ─────────────────────────────────────────────────────

        [Fact]
        public async Task WriteFile_GravaOArquivo()
        {
            string file = Path_("saida.txt");

            string result = await new WriteFileTool().ExecuteAsync(Args(("path", file), ("content", "abc")));

            result.Should().StartWith("SUCESSO");
            (await File.ReadAllTextAsync(file)).Should().Be("abc");
        }

        [Fact]
        public async Task WriteFile_CriaODiretorioQuandoFalta()
        {
            string file = Path.Combine(_dir, "sub", "nested", "saida.txt");

            string result = await new WriteFileTool().ExecuteAsync(Args(("path", file), ("content", "abc")));

            result.Should().StartWith("SUCESSO");
            File.Exists(file).Should().BeTrue();
        }

        [Fact]
        public async Task WriteFile_Sobrescreve()
        {
            string file = Path_("mesmo.txt");
            await File.WriteAllTextAsync(file, "antigo");

            await new WriteFileTool().ExecuteAsync(Args(("path", file), ("content", "novo")));

            (await File.ReadAllTextAsync(file)).Should().Be("novo");
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"path\":\"x\"}")]
        [InlineData("{\"content\":\"x\"}")]
        public async Task WriteFile_ParametroFaltando_Recusa(string argumentsJson)
        {
            string result = await new WriteFileTool().ExecuteAsync(argumentsJson);

            result.Should().StartWith("ERRO");
        }

        [Fact]
        public async Task WriteFile_JsonQuebrado_ViraErroDeTexto_NuncaExcecao()
        {
            Func<Task> act = async () => await new WriteFileTool().ExecuteAsync("nao e json");

            await act.Should().NotThrowAsync();
        }
    }
}

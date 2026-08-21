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

            result.Should().Be("conteúdo esperado");
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
            result.Should().Contain("não encontrado");
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
        public async Task ReadFile_ArquivoGigante_EhTruncadoEmDozeMilCaracteres()
        {
            string file = Path_("grande.txt");
            await File.WriteAllTextAsync(file, new string('x', 20000));

            string result = await new ReadFileTool().ExecuteAsync(Args(("path", file)));

            result.Should().StartWith(new string('x', 12000));
            result.Should().Contain("conteúdo truncado");
            // O corte protege a janela de contexto: o retorno não pode crescer com o arquivo.
            result.Length.Should().BeLessThan(12200);
        }

        [Fact]
        public async Task ReadFile_ArquivoNoLimite_NaoEhTruncado()
        {
            string file = Path_("limite.txt");
            await File.WriteAllTextAsync(file, new string('y', 12000));

            string result = await new ReadFileTool().ExecuteAsync(Args(("path", file)));

            result.Should().NotContain("truncado");
            result.Length.Should().Be(12000);
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
            tool.Name.Should().Be("read_file");
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

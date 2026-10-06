using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O pré-voo e o card do <c>write</c>.
    /// <para>
    /// Sem pré-voo, uma chamada sem <c>content</c> mostrava "CRIAR …" no card, o usuário
    /// autorizava, e só depois vinha o "parâmetros obrigatórios": uma autorização pedida para
    /// algo que não ia acontecer.
    /// </para>
    /// </summary>
    [Collection("Escrita")]
    public class WriteFileToolTests : IDisposable
    {
        private readonly string _dir;

        public WriteFileToolTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aib-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PastasSemConfirmacao.Configurar("");
        }

        public void Dispose()
        {
            PastasSemConfirmacao.Configurar("");
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class PromptQueConta : IConfirmationPrompt
        {
            public int Perguntas { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas++;
                return Task.FromResult((true, false));
            }
        }

        [Fact]
        public async Task SemContent_RecusaNoPreVoo_SemAbrirOCard()
        {
            var prompt = new PromptQueConta();
            var registry = new ToolRegistry(prompt);
            string alvo = Path.Combine(_dir, "a.txt");
            string? decisao = null;

            string r = await registry.ExecuteToolAsync(
                "write", JsonSerializer.Serialize(new { path = alvo }), 9, null, d => decisao = d);

            r.Should().StartWith("ERRO").And.Contain("content");
            prompt.Perguntas.Should().Be(0, "não se pede autorização para o que não vai acontecer");
            decisao.Should().Be("recusada_no_pre_voo");
            File.Exists(alvo).Should().BeFalse();
        }

        [Theory]
        [InlineData("{\"content\":\"x\"}")]
        [InlineData("{\"path\":\"   \",\"content\":\"x\"}")]
        [InlineData("{\"path\":42,\"content\":\"x\"}")]
        [InlineData("{isto nao e json")]
        [InlineData("[]")]
        public void ArgumentosQueNaoServem_SaoRecusadosNoPreVoo(string args)
        {
            ((ITool)new WriteFileTool()).Validar(args).Should().StartWith("ERRO");
        }

        [Fact]
        public void ContentVazio_EhLegitimo()
        {
            // Criar um arquivo vazio é um pedido válido.
            ((ITool)new WriteFileTool())
                .Validar(JsonSerializer.Serialize(new { path = Path.Combine(_dir, "vazio.txt"), content = "" }))
                .Should().BeNull();
        }

        [Fact]
        public void OCard_DiferenciaCRIAR_DeSOBRESCREVER_EMostraOConteudo()
        {
            var tool = new WriteFileTool();
            string novo = Path.Combine(_dir, "novo.txt");
            string velho = Path.Combine(_dir, "velho.txt");
            File.WriteAllText(velho, "antes");

            var criar = tool.BuildConfirmationContext(JsonSerializer.Serialize(new { path = novo, content = "oi" }), 9);
            var sobrescrever = tool.BuildConfirmationContext(JsonSerializer.Serialize(new { path = velho, content = "oi" }), 9);

            criar!.Command.Should().Be("CRIAR " + novo);
            sobrescrever!.Command.Should().Be("SOBRESCREVER " + velho);
            criar.ScriptBody.Should().Be("oi", "o card mostra o que vai ser gravado");
        }

        [Fact]
        public void OCard_SemContent_NaoDescreve()
        {
            // Segunda linha, caso o pré-voo seja pulado: sem content não há o que autorizar.
            new WriteFileTool()
                .BuildConfirmationContext(JsonSerializer.Serialize(new { path = Path.Combine(_dir, "a.txt") }), 9)
                .Should().BeNull();
        }
    }
}

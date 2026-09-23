using System;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Duas coisas do <c>shell</c> que o histórico das sessões cobrou.
    /// <para>
    /// PRÉ-VOO. Sem <c>Validar</c>, um <c>command</c> ausente ou vazio chegava ao card, que não
    /// sabia descrever a operação, e o registry respondia "ACESSO NEGADO: não foi possível
    /// descrever a operação para autorizar". Quem lê "ACESSO NEGADO" conclui que o problema é
    /// permissão: vai trocar de caminho, de ferramenta e de nível, nunca de sintaxe.
    /// </para>
    /// <para>
    /// ACENTO. 56 linhas do histórico voltaram com "Diret�rio" e "conclu��do" porque ninguém
    /// fixava a codificação do processo filho. Isso entra no contexto do modelo, na memória e na
    /// tela — e um nome de arquivo acentuado que volte assim e seja reenviado numa chamada
    /// seguinte é um caminho que não existe.
    /// </para>
    /// </summary>
    public class PreVooEAcentoDoShellTests
    {
        private sealed class PromptQueConta : IConfirmationPrompt
        {
            public int Perguntas { get; private set; }

            public Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
            {
                Perguntas++;
                return Task.FromResult((true, false));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pré-voo
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("{ isto nao e json")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"command\":\"   \"}")]
        [InlineData("{\"command\":42}")]
        public void ChamadaSemComando_EhERRO_NuncaAcessoNegado(string args)
        {
            string? recusa = ((ITool)new RunCommandTool()).Validar(args);

            recusa.Should().StartWith("ERRO", "a tela e a memória leem a primeira palavra");
            recusa.Should().NotContain("ACESSO NEGADO");
            recusa.Should().Contain("command");
        }

        [Fact]
        public void ComandoDeVerdade_PASSA_NoPreVoo()
        {
            ((ITool)new RunCommandTool())
                .Validar("{\"command\":\"docker ps\"}")
                .Should().BeNull();
        }

        [Fact]
        public async Task OErroDeSintaxe_NaoVIRA_PerguntaAoUsuario()
        {
            // Não se pede autorização para o que não vai acontecer.
            var prompt = new PromptQueConta();
            var registry = new ToolRegistry(prompt);
            string? decisao = null;

            string r = await registry.ExecuteToolAsync(
                "shell", "{ isto nao e json", 9, null, d => decisao = d);

            r.Should().StartWith("ERRO");
            r.Should().NotContain("ACESSO NEGADO");
            decisao.Should().Be("recusada_no_pre_voo");
            prompt.Perguntas.Should().Be(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Descrição e schema
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ADescricao_DIZ_OQueNaoEhParaFazerAqui()
        {
            // "Investigar o sistema" cobria listar pasta, ver se arquivo existe, procurar texto
            // e criar pasta — e foi o que aconteceu: 35 de 167 comandos de shell em 49 sessões
            // só tocavam arquivo, com um card de confirmação cada.
            string d = new RunCommandTool().Description;

            d.Should().Contain("não é Bash");
            d.Should().Contain("NÃO use para arquivo");
            d.Should().Contain("write", "a ferramenta que cria a pasta é nomeada aqui");
        }

        [Fact]
        public void OSchema_DIZ_ONDE_OComandoRoda()
        {
            // A instrução antiga era "use 'pwd' ou Get-Location se precisar saber o diretório
            // atual": um turno inteiro para responder algo que o programa já sabe.
            string schema = new RunCommandTool().ChatToolDefinition.FunctionParameters.ToString();

            // E tem de continuar sendo JSON válido: a barra invertida do caminho do Windows não
            // é escape legal em JSON, e sem escapá-la a ferramenta inteira não chega ao modelo.
            var lido = JsonSerializer.Deserialize<JsonElement>(schema);
            string descricao = lido.GetProperty("properties").GetProperty("command")
                .GetProperty("description").GetString()!;

            descricao.Should().Contain(Environment.CurrentDirectory);
        }

        [Fact]
        public void ADescricaoDoWrite_DIZ_QueEleCriaAPasta()
        {
            // O caso do dono: dois cards e dois turnos gastos em New-Item para uma capacidade
            // que a ferramenta já tinha — e que a descrição escondia.
            new WriteFileTool().Description.Should().Contain("Cria sozinho as pastas");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Acentuação — roda powershell.exe de verdade
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void OPrefixo_FIXA_ACodificacaoDoFilho()
        {
            // Dentro de try: sem console anexado, atribuir [Console]::OutputEncoding pode
            // falhar, e uma falha aqui derrubaria TODO comando.
            RunCommandTool.Prefixo.Should().Contain("OutputEncoding");
            RunCommandTool.Prefixo.Should().Contain("try {");
            RunCommandTool.Prefixo.Should().Contain("ProgressPreference",
                "a barra de progresso continua calada, ou o CLIXML volta a entupir o stderr");
        }

        [Fact]
        public async Task OTextoACENTUADO_VoltaIgual()
        {
            string saida = await new RunCommandTool().ExecuteAsync(
                "{\"command\":\"Write-Output 'Diretório: Recepção São José concluído'\"}", 9);

            saida.Should().Contain("Diretório: Recepção São José concluído");
            saida.Should().NotContain("�", "o caractere de substituição é o sintoma");
        }

        [Fact]
        public async Task OAcento_VoltaIgual_TambemNoStderr()
        {
            // O erro do PowerShell viaja dentro do CLIXML, no stderr: se a codificação de lá
            // ficar de fora, a mensagem que o modelo precisa ler é a que chega quebrada.
            string saida = await new RunCommandTool().ExecuteAsync(
                "{\"command\":\"Write-Error 'Não foi possível: operação inválida'\"}", 9);

            saida.Should().StartWith("ERRO");
            saida.Should().Contain("Não foi possível");
            saida.Should().NotContain("�");
        }
    }
}

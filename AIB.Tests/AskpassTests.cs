using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AIB.Services.Terminal;
using AIB.Services.Tools;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O prompt de senha do ssh/git chega ao usuário numa janela do AIB, e não à IA.
    /// </summary>
    public class AskpassTests
    {
        private static readonly string SshDoSistema = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");

        private const string PromptDaSenha = "carlo@srv01's password: ";

        private sealed class Usuario
        {
            public readonly List<PedidoDeSenha> Pedidos = new();
            public Func<PedidoDeSenha, RespostaDoUsuario?> Responde =
                _ => new RespostaDoUsuario("s3nha", false);
            public TimeSpan Demora = TimeSpan.Zero;

            public async Task<RespostaDoUsuario?> Perguntar(PedidoDeSenha p)
            {
                Pedidos.Add(p);
                if (Demora > TimeSpan.Zero) await Task.Delay(Demora);
                return Responde(p);
            }
        }

        private static AskpassServidor Servidor(Usuario u, string? solicitante = null) =>
            new(u.Perguntar, _ => solicitante ?? SshDoSistema);

        // ── O pedido ────────────────────────────────────────────────────────

        [Theory]
        [InlineData("carlo@srv01's password: ", TipoDePedido.Senha)]
        [InlineData("Enter passphrase for key 'C:\\Users\\c\\.ssh\\id_ed25519': ", TipoDePedido.Senha)]
        [InlineData("Username for 'https://github.com': ", TipoDePedido.Texto)]
        [InlineData("Password for 'https://carlo@github.com': ", TipoDePedido.Senha)]
        [InlineData("The authenticity of host 'srv01 (10.0.0.5)' can't be established.\nED25519 key fingerprint is SHA256:abc.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ", TipoDePedido.SimNao)]
        [InlineData("Please type 'yes', 'no' or the fingerprint: ", TipoDePedido.SimNao)]
        public void Classifica_OQueOPromptQuer(string prompt, TipoDePedido tipo)
        {
            AskpassServidor.Classificar(prompt).Should().Be(tipo);
        }

        [Fact]
        public void SoConfia_EmPastaDoSistema()
        {
            // Um ssh.exe em pasta que qualquer um escreve pode ser qualquer coisa com esse nome.
            AskpassServidor.Confiavel(SshDoSistema).Should().BeTrue();
            AskpassServidor.Confiavel(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "ssh.exe"))
                .Should().BeTrue();

            AskpassServidor.Confiavel(@"C:\Users\x\Downloads\ssh.exe").Should().BeFalse();
            AskpassServidor.Confiavel(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                .Should().BeFalse("o próprio shell chamando o AIB.exe não é o ssh pedindo senha");
            AskpassServidor.Confiavel(null).Should().BeFalse();
        }

        // ── O token ─────────────────────────────────────────────────────────

        [Fact]
        public async Task TokenDesconhecido_NaoAbreJanela()
        {
            var u = new Usuario();
            using var s = Servidor(u);

            (await s.ResponderAsync("inventado", PromptDaSenha, 1)).Should().BeNull();
            u.Pedidos.Should().BeEmpty();
        }

        [Fact]
        public async Task ComandoTerminado_TokenMorre()
        {
            var u = new Usuario();
            using var s = Servidor(u);
            var sessao = s.AbrirSessao("ssh carlo@srv01 uptime");
            string token = sessao.Token;
            sessao.Dispose();

            (await s.ResponderAsync(token, PromptDaSenha, 1)).Should().BeNull();
            u.Pedidos.Should().BeEmpty();
        }

        [Fact]
        public async Task TokenVivo_PerguntaAoUsuario_EDevolveOQueEleDigitou()
        {
            var u = new Usuario();
            using var s = Servidor(u);
            using var sessao = s.AbrirSessao("ssh carlo@srv01 uptime");

            (await s.ResponderAsync(sessao.Token, PromptDaSenha, 1)).Should().Be("s3nha");

            u.Pedidos.Should().ContainSingle();
            var p = u.Pedidos[0];
            p.Prompt.Should().Be(PromptDaSenha);
            p.Comando.Should().Be("ssh carlo@srv01 uptime");
            p.Solicitante.Should().Be(SshDoSistema);
            p.SolicitanteConfiavel.Should().BeTrue();
            p.Tipo.Should().Be(TipoDePedido.Senha);
        }

        [Fact]
        public async Task Cancelar_DevolveNull()
        {
            var u = new Usuario { Responde = _ => null };
            using var s = Servidor(u);
            using var sessao = s.AbrirSessao("ssh x");

            (await s.ResponderAsync(sessao.Token, PromptDaSenha, 1)).Should().BeNull();
        }

        // ── Lembrar até fechar ──────────────────────────────────────────────

        [Fact]
        public async Task Lembrada_ServeOProximoComando_SemJanela()
        {
            var u = new Usuario { Responde = _ => new RespostaDoUsuario("s3nha", true) };
            using var s = Servidor(u);

            using (var a = s.AbrirSessao("ssh carlo@srv01 uptime"))
                await s.ResponderAsync(a.Token, PromptDaSenha, 1);

            using var b = s.AbrirSessao("ssh carlo@srv01 df -h");
            (await s.ResponderAsync(b.Token, PromptDaSenha, 1)).Should().Be("s3nha");
            u.Pedidos.Should().HaveCount(1);
        }

        [Fact]
        public async Task Lembrada_NuncaVaiParaOutroHost()
        {
            var u = new Usuario { Responde = _ => new RespostaDoUsuario("s3nha", true) };
            using var s = Servidor(u);

            using (var a = s.AbrirSessao("ssh carlo@srv01 uptime"))
                await s.ResponderAsync(a.Token, PromptDaSenha, 1);

            using var b = s.AbrirSessao("ssh carlo@outro uptime");
            await s.ResponderAsync(b.Token, "carlo@outro's password: ", 1);
            u.Pedidos.Should().HaveCount(2);
        }

        [Fact]
        public async Task Lembrada_NaoVaiParaQuemNaoEhConfiavel()
        {
            // O modelo pode rodar o AIB.exe direto com o prompt de um servidor conhecido: a senha
            // lembrada sairia pela saída do comando, direto para a IA.
            var u = new Usuario { Responde = _ => new RespostaDoUsuario("s3nha", true) };
            string? quem = SshDoSistema;
            using var s = new AskpassServidor(u.Perguntar, _ => quem);

            using (var a = s.AbrirSessao("ssh carlo@srv01 uptime"))
                await s.ResponderAsync(a.Token, PromptDaSenha, 1);

            quem = @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe";
            using var b = s.AbrirSessao("& $env:SSH_ASKPASS \"carlo@srv01's password: \"");
            await s.ResponderAsync(b.Token, PromptDaSenha, 1);

            u.Pedidos.Should().HaveCount(2, "sem janela, a senha lembrada teria ido para a IA");
            u.Pedidos[1].SolicitanteConfiavel.Should().BeFalse();
        }

        [Fact]
        public async Task SemConfianca_NemGuarda()
        {
            var u = new Usuario { Responde = _ => new RespostaDoUsuario("s3nha", true) };
            using var s = Servidor(u, @"C:\Users\x\Downloads\ssh.exe");

            using (var a = s.AbrirSessao("ssh x")) await s.ResponderAsync(a.Token, PromptDaSenha, 1);
            using (var b = s.AbrirSessao("ssh x")) await s.ResponderAsync(b.Token, PromptDaSenha, 1);

            u.Pedidos.Should().HaveCount(2);
        }

        [Fact]
        public async Task LembradaRecusada_PerguntaDeNovo_EEsquece()
        {
            // O ssh repete o prompt quando a senha falha. Responder a lembrada de novo só
            // gastaria as tentativas do servidor.
            int vez = 0;
            var u = new Usuario
            {
                Responde = _ => ++vez == 1 ? new RespostaDoUsuario("velha", true) : new RespostaDoUsuario("nova", false)
            };
            using var s = Servidor(u);

            using (var a = s.AbrirSessao("ssh x")) await s.ResponderAsync(a.Token, PromptDaSenha, 1);

            using (var b = s.AbrirSessao("ssh x"))
            {
                (await s.ResponderAsync(b.Token, PromptDaSenha, 1)).Should().Be("velha");
                (await s.ResponderAsync(b.Token, PromptDaSenha, 1)).Should().Be("nova");
            }

            using var c = s.AbrirSessao("ssh x");
            (await s.ResponderAsync(c.Token, PromptDaSenha, 1)).Should().Be("nova");
            u.Pedidos.Should().HaveCount(3, "a velha foi esquecida e a nova não foi lembrada");
        }

        [Fact]
        public async Task Fingerprint_NuncaEhLembrada()
        {
            const string host = "Are you sure you want to continue connecting (yes/no/[fingerprint])? ";
            var u = new Usuario { Responde = _ => new RespostaDoUsuario("yes", true) };
            using var s = Servidor(u);

            using (var a = s.AbrirSessao("ssh x")) await s.ResponderAsync(a.Token, host, 1);
            using (var b = s.AbrirSessao("ssh x")) await s.ResponderAsync(b.Token, host, 1);

            u.Pedidos.Should().HaveCount(2);
        }

        [Fact]
        public async Task Esquecer_ApagaAsLembradas()
        {
            var u = new Usuario { Responde = _ => new RespostaDoUsuario("s3nha", true) };
            using var s = Servidor(u);

            using (var a = s.AbrirSessao("ssh x")) await s.ResponderAsync(a.Token, PromptDaSenha, 1);
            s.Esquecer();
            using (var b = s.AbrirSessao("ssh x")) await s.ResponderAsync(b.Token, PromptDaSenha, 1);

            u.Pedidos.Should().HaveCount(2);
        }

        // ── O teto do shell ─────────────────────────────────────────────────

        [Fact]
        public async Task TempoNaJanela_EhContado_ParaOTetoDescontar()
        {
            var u = new Usuario { Demora = TimeSpan.FromMilliseconds(150) };
            using var s = Servidor(u);
            using var sessao = s.AbrirSessao("ssh x");

            sessao.TempoComUsuario.Should().Be(TimeSpan.Zero);
            await s.ResponderAsync(sessao.Token, PromptDaSenha, 1);
            sessao.TempoComUsuario.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public void OTeto_DescontaOTempoComOUsuario()
        {
            var prazo = RunCommandTool.Prazo;
            RunCommandTool.Estourou(prazo + TimeSpan.FromSeconds(1), TimeSpan.Zero).Should().BeTrue();
            RunCommandTool.Estourou(prazo + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(62)).Should().BeFalse();
        }

        // ── O ambiente e o pipe ─────────────────────────────────────────────

        [Fact]
        public void Preparar_PoeOAskpassNoAmbiente()
        {
            var u = new Usuario();
            using var s = Servidor(u);
            using var sessao = s.AbrirSessao("ssh x");
            var inicio = new ProcessStartInfo("powershell.exe");

            s.Preparar(inicio, sessao);

            inicio.Environment["SSH_ASKPASS"].Should().Be(Environment.ProcessPath);
            inicio.Environment["SSH_ASKPASS_REQUIRE"].Should().Be("force");
            inicio.Environment["GIT_ASKPASS"].Should().Be(Environment.ProcessPath);
            inicio.Environment[AskpassServidor.VarPipe].Should().Be(s.NomeDoPipe);
            inicio.Environment[AskpassServidor.VarToken].Should().Be(sessao.Token);
        }

        [Fact]
        public void ModoAskpass_SoComAsVariaveis_EComPrompt()
        {
            // Este processo de teste não tem as variáveis: o app sobe normalmente.
            AskpassCliente.EstaNoModoAskpass(new[] { PromptDaSenha }).Should().BeFalse();
        }

        [Fact]
        public async Task IdaEVolta_PeloPipe()
        {
            var u = new Usuario();
            using var s = Servidor(u);
            using var sessao = s.AbrirSessao("ssh carlo@srv01 uptime");

            (await AskpassCliente.PedirAsync(s.NomeDoPipe, sessao.Token, PromptDaSenha))
                .Should().Be("s3nha");
            (await AskpassCliente.PedirAsync(s.NomeDoPipe, "errado", PromptDaSenha))
                .Should().BeNull();

            u.Pedidos.Should().ContainSingle();
        }

        // ── A janela ────────────────────────────────────────────────────────

        [Fact]
        public void Janela_AvisaQuandoQuemPedeNaoEhConfiavel()
        {
            var confiavel = new PedidoDeSenha(PromptDaSenha, "ssh x", SshDoSistema, true, TipoDePedido.Senha);
            var estranho = confiavel with { Solicitante = @"C:\tmp\ssh.exe", SolicitanteConfiavel = false };

            SenhaDoTerminalDialog.Textos(confiavel).Aviso.Should().BeNull();
            SenhaDoTerminalDialog.Textos(confiavel).Explicacao.Should().Contain("ssh.exe");
            SenhaDoTerminalDialog.Textos(estranho).Aviso.Should().Contain("voltar para a IA");
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O reset de fábrica apaga TUDO o que há na raiz de dados.
    /// <para>
    /// Decisão do usuário: reset é total. A versão anterior varria só <c>memory/</c> e
    /// <c>email/</c> e renomeava <c>raw.jsonl</c> e <c>facts.md</c> para <c>.bak</c>; skills,
    /// personagens, logs e o perfil do navegador (com os logins) nem eram tocados. Quem
    /// resetava para entregar a máquina deixava tudo isso para trás.
    /// </para>
    /// <para>
    /// Tudo em raiz temporária. Um ensaio deste assunto que escapasse para o <c>~/.AIB</c> real
    /// apagaria os dados de quem está rodando a suíte.
    /// </para>
    /// </summary>
    public class ResetDeFabricaTests : IDisposable
    {
        private readonly string _raiz =
            Path.Combine(Path.GetTempPath(), "aib-reset-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { }
        }

        /// <summary>Uma raiz de dados com a cara da de verdade.</summary>
        private void Povoar()
        {
            Escrever("memory/sessions/20260920-101500-123/raw.jsonl", "{}");
            Escrever("memory/sessions/20260920-101500-123/chapters.jsonl", "{}");
            Escrever("memory/sessions/antiga/raw.20260901-0900.jsonl.bak", "{}");
            Escrever("memory/facts.md", "# fatos");
            Escrever("email/estado.json", "{}");
            Escrever("email/conversas/abc/triagem.jsonl", "{}");
            Escrever("profile.dat", "cifrado");
            Escrever("chat_history.json", "[]");
            Escrever("credentials/openrouter.bin", "cifrado");
            Escrever("logs/audit-2026-09-20.jsonl", "{}");
            Escrever("skills/planilha/SKILL.md", "# skill");
            Escrever("character/Ayano/SOUL.MD", "# alma");
            Escrever("navegador/sites-liberados.txt", "exemplo.com");
            Escrever("navegador/perfil/Default/Cookies", "sessao");
        }

        private const int ArquivosPovoados = 14;

        private string Caminho(string relativo) =>
            Path.Combine(_raiz, relativo.Replace('/', Path.DirectorySeparatorChar));

        private void Escrever(string relativo, string conteudo)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho(relativo))!);
            File.WriteAllText(Caminho(relativo), conteudo);
        }

        [Fact]
        public void TUDO_Some_ESoARaizVaziaFica()
        {
            // Inclui o que o reset antigo preservava (raw.jsonl, facts.md, .bak de resets
            // anteriores, logs) e o que ele nem tocava (skills, personagens, navegador).
            Povoar();

            var r = ResetDeFabrica.Limpar(_raiz);

            Directory.GetFileSystemEntries(_raiz).Should().BeEmpty();
            r.Apagados.Should().Be(ArquivosPovoados);
            r.Falhas.Should().Be(0);
        }

        [Fact]
        public void ARaiz_FICA_ParaAMigracaoAntigaNaoVoltar()
        {
            // Sem a pasta ~/.AIB, o arranque migra de volta o que houver em %AppData%\AIB (o
            // local de antes): os dados apagados reapareceriam.
            Povoar();

            ResetDeFabrica.Limpar(_raiz);

            Directory.Exists(_raiz).Should().BeTrue();
        }

        [Fact]
        public void ArquivoSO_LEITURA_TambemSome()
        {
            // O perfil do Edge e skills vindas de um repositório trazem arquivos só-leitura, e
            // o File.Delete os recusa: a pasta navegador/ sobrava do reset.
            Escrever("navegador/perfil/Default/Preferences", "{}");
            File.SetAttributes(Caminho("navegador/perfil/Default/Preferences"), FileAttributes.ReadOnly);

            var r = ResetDeFabrica.Limpar(_raiz);

            r.Falhas.Should().Be(0);
            Directory.GetFileSystemEntries(_raiz).Should().BeEmpty();
        }

        [Fact]
        public void NADA_ForaDaRAIZ_EhTocado()
        {
            // A raiz é parâmetro justamente para isto poder ser provado.
            string vizinha = _raiz + "-vizinha";
            Directory.CreateDirectory(vizinha);
            File.WriteAllText(Path.Combine(vizinha, "raw.jsonl"), "de outro");

            try
            {
                Povoar();
                ResetDeFabrica.Limpar(_raiz);

                File.Exists(Path.Combine(vizinha, "raw.jsonl")).Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(vizinha, true); } catch { }
            }
        }

        [Fact]
        public void AtalhoDePasta_SaiSemLevarODestino()
        {
            // Uma skill pode ser uma junção para a pasta de um projeto. Descer por ela apagaria
            // o projeto, que não é do AIB.
            string destino = _raiz + "-destino";
            Directory.CreateDirectory(destino);
            File.WriteAllText(Path.Combine(destino, "trabalho.txt"), "do usuário");
            Directory.CreateDirectory(Caminho("skills"));

            try
            {
                string juncao = Caminho("skills/projeto");
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{juncao}\" \"{destino}\"")
                { CreateNoWindow = true, UseShellExecute = false })!)
                {
                    p.WaitForExit();
                }
                Directory.Exists(juncao).Should().BeTrue("o ensaio depende da junção criada");

                ResetDeFabrica.Limpar(_raiz);

                Directory.Exists(juncao).Should().BeFalse();
                File.Exists(Path.Combine(destino, "trabalho.txt")).Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(destino, true); } catch { }
            }
        }

        [Fact]
        public void RaizQueNAO_EXISTE_NaoEhErro()
        {
            Action limpar = () => ResetDeFabrica.Limpar(Path.Combine(_raiz, "nunca-existiu"));

            limpar.Should().NotThrow();
        }

        [Fact]
        public void ArquivoTRAVADO_NaoDerruba_NemABORTA_ORestante()
        {
            // Um arquivo em uso derrubaria o reset pela metade — e a metade que morre é sempre a
            // que ainda não rodou. A falha vira linha no console e o resto segue.
            Povoar();

            ResetDeFabrica.Resultado r = null!;
            using (new FileStream(Caminho("memory/facts.md"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Action limpar = () => r = ResetDeFabrica.Limpar(_raiz);
                limpar.Should().NotThrow();
            }

            r.Falhas.Should().Be(1);
            r.Apagados.Should().Be(ArquivosPovoados - 1);
            File.Exists(Caminho("memory/facts.md")).Should().BeTrue();
            Directory.Exists(Caminho("skills")).Should().BeFalse("o que vem depois do arquivo travado ainda tem de acontecer");
            Directory.Exists(Caminho("navegador")).Should().BeFalse();
        }

        [Fact]
        public void PastaQueNaoEhSoDoAIB_EhRECUSADA()
        {
            // A raiz de dados é configurável (DataDirectory). "Apagar tudo" apontado para o
            // perfil do usuário, para a raiz do disco ou para a pasta do programa apagaria o que
            // não é do AIB.
            string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            ResetDeFabrica.RaizSegura(perfil).Should().BeFalse();
            ResetDeFabrica.RaizSegura(Path.GetPathRoot(perfil)!).Should().BeFalse();
            ResetDeFabrica.RaizSegura(Path.GetDirectoryName(perfil)!).Should().BeFalse("contém o perfil");
            ResetDeFabrica.RaizSegura(AppContext.BaseDirectory).Should().BeFalse();
            ResetDeFabrica.RaizSegura("").Should().BeFalse();

            ResetDeFabrica.RaizSegura(Path.Combine(perfil, ".AIB")).Should().BeTrue();
            ResetDeFabrica.RaizSegura(_raiz).Should().BeTrue();
        }

        [Fact]
        public void RaizRecusada_NaoApagaNada_EContaAFalha()
        {
            var r = ResetDeFabrica.Limpar(AppContext.BaseDirectory);

            r.Should().Be(new ResetDeFabrica.Resultado(0, 1));
            File.Exists(typeof(ResetDeFabricaTests).Assembly.Location).Should().BeTrue();
        }
    }
}

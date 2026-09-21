using System;
using System.Collections.Generic;
using System.Linq;
using AIB.Services;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A gravação da conversa no histórico.
    /// <para>
    /// Existe por um defeito de verdade: o histórico só era escrito no ResetHistory — fechar a
    /// janela, começar conversa nova, trocar de personagem. Uma conversa interrompida de
    /// qualquer outra forma sumia inteira, e o usuário não tinha como saber que ela nunca
    /// chegou a ser gravada.
    /// </para>
    /// <para>
    /// O arquivo é um só para a suíte inteira, que roda classes em paralelo. Por isso cada
    /// ensaio usa ids e textos próprios e afirma só sobre o que ele mesmo gravou: trocar a
    /// pasta do histórico no meio faria os ensaios das outras classes gravarem nela.
    /// </para>
    /// </summary>
    [Collection("Historico")]
    public class ChatHistoryArchiveTests
    {
        private static readonly string Marca = Guid.NewGuid().ToString("N")[..8];

        private static string Id(string sufixo) => $"ensaio-{Marca}-{sufixo}";

        private static List<ChatMessage> Conversa(params string[] falas)
        {
            var lista = new List<ChatMessage> { ChatMessage.CreateSystemMessage("prompt") };

            for (int i = 0; i < falas.Length; i++)
            {
                lista.Add(i % 2 == 0
                    ? ChatMessage.CreateUserMessage(falas[i])
                    : ChatMessage.CreateAssistantMessage(falas[i]));
            }

            return lista;
        }

        private static ChatSession? Buscar(string id) =>
            ChatHistoryService.LoadHistory().FirstOrDefault(h => h.Id == id);

        [Fact]
        public void SessaoGravada_LevaAPastaDaMemoriaJunto()
        {
            // Sem este campo nao ha como voltar aos capitulos de uma conversa: o Id daqui e um
            // Guid e a pasta da memoria e um carimbo de tempo, e nada os relacionava.
            string id = Id("memoria");

            ChatHistoryService.SaveCurrentSession(
                Conversa("com pasta de memoria", "ola"), "titulo", id,
                memorySessionId: "20260901-120000-000");

            Buscar(id)!.MemorySessionId.Should().Be("20260901-120000-000");
        }

        [Fact]
        public void SemPastaDeMemoria_OCampoFicaVazio()
        {
            string id = Id("sem-memoria");

            // Texto proprio: o arquivador recusa gravar conteudo identico ao de outra sessao,
            // e reaproveitar as falas do ensaio anterior faria este aqui nao gravar nada.
            ChatHistoryService.SaveCurrentSession(Conversa("sem pasta de memoria", "ola"), "titulo", id);

            Buscar(id)!.MemorySessionId.Should().BeEmpty();
        }

        [Fact]
        public void GravarEReler_DevolveAConversa()
        {
            string id = Id("roundtrip");
            ChatHistoryService.SaveCurrentSession(Conversa($"oi {id}", "olá"), "Primeiro contato", id);

            var sessao = Buscar(id);

            sessao.Should().NotBeNull();
            sessao!.Title.Should().Be("Primeiro contato");
            sessao.Content.Should().Contain($"USER: oi {id}").And.Contain("AIB: olá");
        }

        [Fact]
        public void MesmoId_SubstituiEmVezDeDuplicar()
        {
            // É o que permite arquivar a cada turno: a conversa viva é regravada por cima de si
            // mesma. Sem isto ela apareceria na lista uma vez por turno.
            string id = Id("upsert");

            ChatHistoryService.SaveCurrentSession(Conversa($"oi {id}", "olá"), null, id);
            ChatHistoryService.SaveCurrentSession(
                Conversa($"oi {id}", "olá", "e aí?", "tudo bem"), null, id);

            ChatHistoryService.LoadHistory().Count(h => h.Id == id)
                .Should().Be(1, "a conversa é uma só");

            Buscar(id)!.Content.Should().Contain("e aí?", "a versão gravada é a mais recente");
        }

        [Fact]
        public void ConteudoIdentico_SemMesmoId_NaoDuplica()
        {
            // Abrir uma conversa antiga põe o conteúdo dela no histórico vivo; arquivar de novo
            // criaria uma cópia a cada ida e volta pelo painel.
            string primeiro = Id("copia-1");
            string segundo = Id("copia-2");
            var conversa = Conversa($"assunto {primeiro}", "certo");

            ChatHistoryService.SaveCurrentSession(conversa, null, primeiro);
            ChatHistoryService.SaveCurrentSession(conversa, null, segundo);

            Buscar(primeiro).Should().NotBeNull();
            Buscar(segundo).Should().BeNull("conteúdo idêntico não entra duas vezes");
        }

        [Fact]
        public void SoOPromptDeSistema_NaoViraConversa()
        {
            string id = Id("vazia");

            ChatHistoryService.SaveCurrentSession(
                new List<ChatMessage> { ChatMessage.CreateSystemMessage("prompt") }, null, id);

            Buscar(id).Should().BeNull();
        }

        // ─────────────────────────────────────────────────────────────────
        // A quarta saída para o disco
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void CorpoDeEmailEmbrulhado_NaoCHEGA_AoArquivo()
        {
            // chat_history.json é disco como o raw.jsonl e o registro de execução, e era a única
            // saída sem o redator. O que o modelo citar de um mail_read não pode ir para cá.
            string id = Id("redigido");
            const string Segredo = "SENHA-DO-COFRE-DA-EMPRESA-7781";

            string corpo = AIB.Services.Mail.ConteudoDeTerceiros.Embrulhar(Segredo);

            ChatHistoryService.SaveCurrentSession(
                Conversa($"leia o e-mail {id} {corpo}", $"o e-mail diz: {corpo} — e só."),
                "com corpo", id);

            var sessao = Buscar(id);
            sessao.Should().NotBeNull();
            sessao!.Content.Should().NotContain(Segredo)
                .And.NotContain(AIB.Services.Mail.ConteudoDeTerceiros.Inicio)
                .And.Contain(AIB.Services.Mail.ConteudoDeTerceiros.Omitido);

            // O arquivo inteiro, e não só o campo: o título também sai da primeira fala.
            string arquivo = System.IO.File.ReadAllText(System.IO.Path.Combine(
                ChatHistoryService.ResolverDiretorio(ChatHistoryService.HistoryDirectoryOverride),
                "chat_history.json"));

            arquivo.Should().NotContain(Segredo);
        }

        // ─────────────────────────────────────────────────────────────────
        // O corte — pura, para não atropelar o arquivo da suíte
        // ─────────────────────────────────────────────────────────────────

        private static ChatSession Sessao(int minutos, string chaveDoEmail = "") => new()
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = new DateTime(2026, 9, 1, 12, 0, 0).AddMinutes(minutos),
            Content = "USER: x",
            MailThreadKey = chaveDoEmail
        };

        [Fact]
        public void CaixaMovimentada_NaoEXPULSA_AsConversasDoUsuario()
        {
            // O corte contava as de e-mail, que ficam fora da lista do painel: abrir e-mail atrás
            // de e-mail tirava do arquivo as conversas que o usuário começou.
            var doUsuario = Enumerable.Range(0, 10).Select(i => Sessao(i)).ToList();
            var deEmail = Enumerable.Range(100, 80)
                .Select(i => Sessao(i, $"eu@x.com|thr:{i}")).ToList();

            var podado = ChatHistoryService.Podar(deEmail.Concat(doUsuario).ToList());

            podado.Where(h => h.MailThreadKey == "").Should().HaveCount(10, "todas as do usuário ficam");
            podado.Count(h => h.MailThreadKey != "")
                .Should().Be(ChatHistoryService.MaxConversasDeEmail, "as de e-mail têm teto próprio");

            podado.Where(h => h.MailThreadKey != "").Min(h => h.Timestamp)
                .Should().Be(deEmail.OrderByDescending(h => h.Timestamp)
                    .ElementAt(ChatHistoryService.MaxConversasDeEmail - 1).Timestamp,
                    "saem as de e-mail mais antigas");
        }

        [Fact]
        public void ConversasDoUsuario_TemTetoProprio_EAsMaisRecentesFicam()
        {
            var doUsuario = Enumerable.Range(0, ChatHistoryService.MaxConversasDoUsuario + 5)
                .Select(i => Sessao(i)).ToList();
            var umaDeEmail = Sessao(-1000, "eu@x.com|uid:5");

            var podado = ChatHistoryService.Podar(doUsuario.Append(umaDeEmail).ToList());

            podado.Count(h => h.MailThreadKey == "").Should().Be(ChatHistoryService.MaxConversasDoUsuario);
            podado.Should().Contain(umaDeEmail, "a de e-mail não disputa lugar com as do usuário");
            podado.Should().NotContain(doUsuario.Take(5), "as mais antigas do usuário saem");
        }

        [Fact]
        public void OCorte_MantemAOrdemDoArquivo()
        {
            // O arquivo guarda a mais nova no topo (Persistir insere no índice 0). O corte só
            // tira linhas; reordenar mudaria a lista do painel sem ninguém ter pedido.
            var lista = new List<ChatSession> { Sessao(1), Sessao(3, "eu@x.com|thr:1"), Sessao(2) };

            ChatHistoryService.Podar(lista).Should().Equal(lista);
        }
    }
}

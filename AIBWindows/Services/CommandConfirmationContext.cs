namespace AIB.Services {
    public class CommandConfirmationContext {
        public string Command { get; set; } = "";
        public string Tool { get; set; } = "";
        public int Level { get; set; }
        public string Cwd { get; set; } = "";
        public bool DenylistHit { get; set; }
        public string DenylistReason { get; set; } = "";
        public string ScriptBody { get; set; } = "";

        /// <summary>
        /// Há texto original de e-mail no contexto da conversa. O card avisa que o pedido pode
        /// ter vindo de instruções escritas no e-mail, e não do usuário.
        /// </summary>
        public bool ConteudoDeEmailNoContexto { get; set; }

        /// <summary>
        /// Do texto de terceiros no contexto, algum é e-mail (e não só página do navegador).
        /// </summary>
        public bool EmailNoContexto { get; set; }

        /// <summary>
        /// Aviso extra do card, quando há o que avisar. Hoje vem do shell: o comando cita
        /// caminho fora das pastas que o usuário marcou como de confiança.
        /// </summary>
        public string Aviso { get; set; } = "";

        /// <summary>
        /// A chave do "sempre permitir" quando ela não é o comando exato. Null: o comando, byte a
        /// byte (o shell). O navegador usa o SITE — "sempre permitir" num clique vale para os
        /// cliques naquele domínio até o app fechar, e não para aquele botão só.
        /// </summary>
        public string? ChaveDeSempre { get; set; }

        /// <summary>
        /// Esta operação nunca entra no "sempre permitir": pergunta toda vez. No navegador, botão
        /// que apaga, conclui, envia, paga; e abrir site novo (aprovar já libera o site).
        /// </summary>
        public bool SemSempre { get; set; }

        /// <summary>
        /// O "sempre" desta operação só se marca SEGURANDO o clique na caixa por 5 s. No
        /// navegador, botão que decide (aprovar, salvar, enviar...): o "sempre" vale para aquele
        /// botão naquele site, e um gesto longo não se dá sem querer no meio de vários cartões.
        /// </summary>
        public bool SempreSegurando { get; set; }

        /// <summary>
        /// O "sempre" desta ferramenta continua valendo com texto de terceiros no contexto. Só o
        /// navegador: o texto de terceiros dele é a própria página que o usuário liberou, e sem
        /// isto todo clique perguntaria. O que é perigoso já chega com <see cref="SemSempre"/>.
        /// Com E-MAIL no contexto não vale: quem escreveu o e-mail poderia mandar clicar.
        /// </summary>
        public bool SempreApesarDeTerceiros { get; set; }
    }
}

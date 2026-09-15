namespace AIB.Services {
    public class CommandConfirmationContext {
        public string Command { get; set; } = "";
        public string Tool { get; set; } = "";
        public int Level { get; set; }
        public string Cwd { get; set; } = "";
        public bool DenylistHit { get; set; }
        public string DenylistReason { get; set; } = "";
        public string ScriptBody { get; set; } = "";
        public string Interpreter { get; set; } = "";

        /// <summary>
        /// Há texto original de e-mail no contexto da conversa. O card avisa que o pedido pode
        /// ter vindo de instruções escritas no e-mail, e não do usuário.
        /// </summary>
        public bool ConteudoDeEmailNoContexto { get; set; }
    }
}

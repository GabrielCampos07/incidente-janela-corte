# Comunicação com Relacionamento

## Hoje (posição inicial, causa ainda em apuração)

> Confirmamos instabilidade no portal ontem a partir das 14h12, e ela pode ter feito ordens enviadas antes das 14h30 serem registradas com horário posterior. Temos um registro independente do horário de chegada de cada requisição (os logs de entrada da AWS, que não dependem do processamento) e estamos cruzando as ordens uma a uma.
>
> Até amanhã às 12h entregamos cada uma das 37 classificada em três grupos: recebida antes do corte, recebida depois, ou tentativa antes do corte com reenvio depois. Para as recebidas antes do corte, a recomendação técnica é tratá-las como D0; a forma de reparação será definida com Operações e Compliance.
>
> Também vamos procurar ativamente outras ordens na mesma situação que ainda não geraram reclamação. A causa ainda está em apuração, e não atribuiremos a falha ao distribuidor sem evidência.

O que esta mensagem evita de propósito: prometer ressarcimento (não é decisão de engenharia), afirmar a causa (ainda não está confirmada) e dizer que o distribuidor errou.

## Amanhã às 12h (modelo de entrega)

> Segue a análise das ordens de 22/10 registradas após o corte.
>
> - Analisadas: N ordens (as 37 reclamadas e outras M registradas entre 14h30 e 14h45).
> - Grupo A, recebidas antes das 14h30: X ordens, das quais Y reclamadas e Z não reclamadas. Recomendação: D0.
> - Grupo B, tentativa antes das 14h30 com falha do portal e reenvio depois: X ordens. Recomendação técnica: D0, pendente de decisão de Compliance.
> - Grupo C, recebidas depois das 14h30: X ordens. Recomendação: manter D+1; segue o horário de chegada de cada uma como evidência para o distribuidor.
> - Sem evidência: X ordens, encaminhadas a Compliance.
>
> Para os grupos A e B, a planilha anexa traz valor, cota de D0, cota de D+1 e diferença por ordem, para Operações definir a reparação. As ordens não reclamadas do grupo A merecem contato proativo.
>
> A análise foi conferida por duas pessoas. A causa técnica está documentada no post-mortem, que compartilhamos até DD/MM.

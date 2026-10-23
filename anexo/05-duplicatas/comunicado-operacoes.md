# Duplicatas: o que levar a Operações hoje

## Por que hoje

Uma aplicação ou resgate duplicado que passa pelo cálculo da cota vira movimentação real no fundo. Cancelar antes é barato; estornar depois envolve o administrador, o distribuidor e o investidor.

## O que entregar

Planilha gerada por `duplicatas.sql`, separada em duas abas, sem CPF nem nome do investidor (identificar por ID da ordem e código do cotista):

1. **Tipo A, mesma ordem enviada mais de uma vez ao processamento.** Erro nosso, sem ambiguidade: a ordem é uma só. Pedido: cancelar no sistema de processamento todos os protocolos exceto o primeiro.
2. **Tipo B, ordens diferentes com mesmo conteúdo em menos de 5 minutos.** Provável clique repetido, mas pode ser uma ordem legítima. Pedido: Relacionamento confirma com cada distribuidor quais manter antes de qualquer cancelamento.

## Mensagem (canal de Operações, com cópia para o gestor da squad)

> Identificamos ordens duplicadas no incidente de ontem e precisamos de apoio para cancelar antes do cálculo da cota de hoje.
>
> - Tipo A: X ordens enviadas mais de uma vez ao sistema de processamento pelo mesmo ID. São duplicatas certas; pedimos o cancelamento de todos os protocolos exceto o primeiro (lista anexa, com o protocolo a manter destacado).
> - Tipo B: Y pares de ordens com mesmo distribuidor, cotista, fundo e valor em menos de 5 minutos. Provável reenvio por falha do portal, mas não temos certeza. Relacionamento está confirmando com os distribuidores; enviamos a lista confirmada até HH:MM.
>
> Qual o horário limite para cancelamento hoje? Fico disponível para conferir item a item.

## O que não fazer

- Cancelar Tipo B sem confirmação do distribuidor.
- Cancelar pelo portal ou direto no banco: o cancelamento precisa acontecer no sistema de processamento, pelo fluxo de Operações, com registro de quem pediu e por quê.
- Reenviar ordens da DLQ antes de a lista de duplicatas estar resolvida.

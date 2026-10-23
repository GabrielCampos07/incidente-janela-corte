# Agenda própria, depois de estabilizar

| Ordem | Item | Impacto | Esforço | Arquivo |
|---|---|---|---|---|
| 1 | Segredos, menor privilégio, dados de investidores fora dos logs | Risco de dados e legal (LGPD); irreversível se vazar | Dias | `segredos-e-dados.md` |
| 2 | Alertas, SLO e runbooks | Confiabilidade: o incidente foi descoberto pelo cliente | Dias | `alarmes.yaml`, `entrega-segura.md` |
| 3 | Correlation ID, log estruturado, tracing | Velocidade de diagnóstico: hoje não se rastreia uma ordem | 1 a 2 sprints | `correlation-id.md` |
| 4 | Feature flag, deploy canário, congelamento, rollback ensaiado | Risco de mudança; também acelera o roadmap | 1 a 2 sprints | `entrega-segura.md` |

Publicar os alarmes:

```bash
aws cloudformation deploy --template-file alarmes.yaml --stack-name portal-alarmes \
  --parameter-overrides PlantaoEndpoint=https://... OperacoesEmail=operacoes@...
```

O alarme `portal-atraso-registro-ordem-5s` depende de a API publicar a métrica `Portal/Ordens AtrasoRegistroMs` (formato EMF no log), que entra junto com a correção do horário de corte.

## Como levo ao PO

- **Custo do incidente:** 37 reclamações formais, reparação financeira, horas de Relacionamento e Operações, exposição regulatória e a squad parada por dois dias.
- **Pedido:** cerca de 20% da capacidade da sprint por 3 sprints, não uma parada do roadmap.
- **Primeiro as vitórias rápidas:** alarmes e rotação de credencial saem em dias.
- **Métrica de sucesso:** tempo para detectar um incidente, de "o cliente avisou" para minutos.
- **Ganho para o roadmap:** flags e deploy canário permitem lançar funcionalidade nova com menos risco, ou seja, mais rápido.
- **Item 1 não é negociável com o PO:** segredos e LGPD vão para Segurança e para o DPO.

## Riscos não citados no cenário

- **Dupla escrita banco e SQS:** resolvida pela outbox em `07-raiz/idempotencia`.
- **Retenção da DLQ contada do envio original:** perda silenciosa de dados. Ver `04-dlq`.
- **Health check dependente do banco:** cascata que derruba as tasks quando o pool esgota. O health check de liveness não deve consultar o banco.
- **Webhooks sem assinatura e URLs sem validação:** autenticidade e SSRF. Ver `07-raiz/integrador`.
- **Rate limit por distribuidor:** sem ele, um único cliente derruba os outros 379.
- **Reconciliação diária com o sistema de processamento:** não existe. Se existisse, as duplicadas teriam aparecido no mesmo dia.
- **Lambda de relatórios no primário:** concorre com o horário de pico.
- **Backup e restauração pontual (PITR) nunca testados.**
- **Fuso, calendário e corte por fundo:** Fargate em UTC, feriados e cortes diferentes por regulamento.

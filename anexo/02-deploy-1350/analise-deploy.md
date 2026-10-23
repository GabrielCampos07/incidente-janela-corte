# Deploy de 22/10 às 13h50: causa ou coincidência

Tempo máximo para esta análise antes da janela: 15 minutos. Se não houver conclusão, a decisão padrão é **não reverter** (ver critério no final).

## 1. Qual serviço subiu

```bash
for svc in api-ordens integrador front; do
  aws ecs describe-services --cluster $CLUSTER --services $svc \
    --query 'services[0].deployments[].{rev:taskDefinition,criado:createdAt,status:rolloutState}'
  aws ecs describe-services --cluster $CLUSTER --services $svc \
    --query "services[0].events[?createdAt>='2026-10-22T16:30:00Z' && createdAt<='2026-10-22T18:30:00Z'].[createdAt,message]" \
    --output text
done
```

Horários em UTC: 13h50 em Brasília é 16h50 UTC.

Os eventos do serviço também mostram `unhealthy`, `stopped` e `has started N tasks`. Se aparecerem tasks paradas por health check entre 14h12 e 14h45, a hipótese de cascata de health check (H6) ganha força, independente do deploy.

## 2. O que mudou no código

```bash
# commits entre a versão anterior e a das 13h50 (ajustar as tags)
git log --oneline release-anterior..release-2210-1350
git diff --stat release-anterior..release-2210-1350

# arquivos que importam para o incidente
git diff release-anterior..release-2210-1350 -- \
  '**/*Posic*' '**/*Enquadr*' '**/*Ordem*' '**/Migrations/**' \
  '**/appsettings*.json' '**/Program.cs' '**/*Health*' \
  'src/integrador/**' 'Dockerfile*'
```

Migrations aplicadas no banco depois das 13h50:

```sql
SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 5;
```

## 3. O que mudou na configuração

```bash
aws ecs describe-task-definition --task-definition api-ordens:REV_NOVA     > td-nova.json
aws ecs describe-task-definition --task-definition api-ordens:REV_ANTERIOR > td-anterior.json
diff <(jq -S '.taskDefinition | del(.revision,.registeredAt,.taskDefinitionArn)' td-anterior.json) \
     <(jq -S '.taskDefinition | del(.revision,.registeredAt,.taskDefinitionArn)' td-nova.json)
```

Itens a olhar no diff: cpu/memória, variáveis com connection string (`Max Pool Size`, `Timeout`, `Command Timeout`), health check, `stopTimeout`.

Atenção: esses JSONs contêm a credencial do banco em texto plano. Não colar no Slack nem em ferramentas de IA. Apagar os arquivos depois.

## 4. O que os dados dizem

No painel de `03-timeline`, comparar 13h30–13h50 com 13h50–14h10:

| Evidência | Aponta para o deploy | Aponta para coincidência |
|---|---|---|
| Latência por requisição com o mesmo volume | Degrau em 13h50 | Estável até o volume subir |
| Top SQL no Performance Insights | Consulta nova após 13h50 | Mesmas consultas, mesmo custo por chamada |
| p95 entre 14h e 14h30 nos últimos 10 dias úteis | Estável até 21/10, salto em 22/10 | Subindo dia a dia desde a prévia |
| Diff | Mexe em consulta, prévia, pool, índice, health check | Não toca o caminho crítico |
| Homologação com o perfil de carga do pico | Versão nova degrada, anterior não | Ambas degradam |

Mesmo sendo coincidência, registrar: o deploy reiniciou as tasks (pool e cache vazios) e, se incluiu o integrador, matou mensagens em processamento. Isso conta para a análise da DLQ (`04-dlq`).

## 5. Critério de decisão

```
o diff toca o caminho crítico (consulta, prévia, pool, índice, health check, POST /ordens)?
├─ não  -> não reverter. Registrar "deploy descartado como gatilho, pendente confirmação com dados".
└─ sim
   ├─ houve migration de banco nessa versão?
   │   ├─ sim -> não reverter antes da janela (rollback de schema não é reversível em minutos).
   │   │         Compensar com a contenção (prévia limitada).
   │   └─ não -> reverter para a revisão anterior da task definition:
   │             aws ecs update-service --cluster $CLUSTER --service api-ordens \
   │               --task-definition api-ordens:REV_ANTERIOR
   │             aguardar rolloutState=COMPLETED antes de T-30 min.
   └─ o rollback não termina antes de T-30 min? -> não iniciar.
```

Por que a decisão padrão é não reverter: um rollback também é deploy e reinicia as tasks perto do pico, que é exatamente um dos efeitos que agravaram o incidente. Só vale o risco se houver evidência de que a versão nova é pior.

## Registro da decisão (preencher)

- Serviço(s) do deploy:
- Diff toca caminho crítico: sim / não. Arquivos:
- Migration: sim / não
- Evidência de métrica:
- Decisão: reverter / não reverter
- Quem decidiu e horário:

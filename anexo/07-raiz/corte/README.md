# Horário de corte decidido pela chegada da requisição

## O que muda

| Hoje | Depois |
|---|---|
| Horário gravado depois do commit | `recebida_em` vem da chegada no API Gateway |
| D0/D+1 decidido pelo horário de gravação | D0/D+1 decidido por `recebida_em`, no fuso de Brasília, com calendário de dias úteis |
| Coluna única de horário | `recebida_em` (imutável) separada de `registrada_em` |

## Configuração no API Gateway (REST, integração HTTP proxy via VPC link)

O mapeamento de integração sobrescreve o header vindo do cliente:

```bash
aws apigateway update-integration --rest-api-id $REST_API_ID \
  --resource-id <id do recurso /ordens> --http-method POST \
  --patch-operations \
    'op=add,path=/requestParameters/integration.request.header.X-Recebida-Em-Epoch,value=context.requestTimeEpoch'
aws apigateway create-deployment --rest-api-id $REST_API_ID --stage-name $STAGE
```

Esse header só serve de prova se o security group do serviço no ECS aceitar tráfego apenas do VPC link (ou do balanceador atrás dele). Se der pra chamar o serviço direto, o cliente forja o header; nesse caso o backend deve rejeitar requisição sem o header e a regra de rede precisa ser corrigida antes de confiar em `recebida_em`.

## Migration

```sql
ALTER TABLE ordens ADD COLUMN recebida_em timestamptz;
-- ordens antigas ficam com NULL: não inventar horário de chegada para o histórico.
-- depois que todas as ordens novas tiverem o valor, em outra migration:
-- ALTER TABLE ordens ADD CONSTRAINT ordens_recebida_em_obrigatoria CHECK (recebida_em IS NOT NULL) NOT VALID;
```

## Decisão que não é de engenharia

Se a primeira tentativa chegou antes do corte, falhou sem gravar e o reenvio (com a mesma `Idempotency-Key`) chegou depois, qual horário vale? Com a idempotência (`../idempotencia`), dá para registrar a primeira chegada por chave. A recomendação técnica é usar a primeira chegada, porque a falha foi do portal; a política precisa ser aprovada por Compliance antes de entrar no código.

## Teste

`JanelaDeCorteTests.cs` cobre os limites (14:29:59.999 e 14:30:00.000), entrada em UTC, sexta-feira, feriado e a regressão do incidente: chegada às 14h29m40s com processamento de 45 segundos continua em D0.

Pacotes de teste: `xunit`, `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.AspNetCore.Http` (já vem com o SDK Web).

# ZAP Scanning Report

ZAP by [Checkmarx](https://checkmarx.com/).


## Summary of Alerts

| Risk Level | Number of Alerts |
| --- | --- |
| High | 0 |
| Medium | 0 |
| Low | 1 |
| Informational | 4 |




## Insights

| Level | Reason | Site | Description | Statistic |
| --- | --- | --- | --- | --- |
| Low | Exceeded High | http://host.docker.internal:5067 | Percentage of responses with status code 4xx | 97 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of responses with status code 2xx | 2 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of responses with status code 5xx | 12 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of endpoints with content type application/json | 16 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of endpoints with content type application/problem+json | 52 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of endpoints with method DELETE | 10 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of endpoints with method GET | 66 % |
| Info | Informational | http://host.docker.internal:5067 | Percentage of endpoints with method POST | 23 % |
| Info | Informational | http://host.docker.internal:5067 | Count of total endpoints | 104    |
| Info | Exceeded Low | http://host.docker.internal:5067 | Percentage of slow responses | 13 % |







## Alerts

| Name | Risk Level | Number of Instances |
| --- | --- | --- |
| A Server Error response code was returned by the server | Low | 1 |
| A Client Error response code was returned by the server | Informational | 112 |
| Authentication Request Identified | Informational | 1 |
| Non-Storable Content | Informational | Systemic |
| Storable and Cacheable Content | Informational | Systemic |




## Alert Detail



### [ A Server Error response code was returned by the server ](https://www.zaproxy.org/docs/alerts/100000/)



##### Low (High)

### Description

A response code of 503 was returned by the server.
This may indicate that the application is failing to handle unexpected input correctly.
Raised by the 'Alert on HTTP Response Code Error' script

* URL: http://host.docker.internal:5067/api/demo/resilience
  * Node Name: `http://host.docker.internal:5067/api/demo/resilience`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `503`
  * Other Info: ``


Instances: 1

### Solution



### Reference



#### CWE Id: [ 388 ](https://cwe.mitre.org/data/definitions/388.html)


#### WASC Id: 20

#### Source ID: 4

### [ A Client Error response code was returned by the server ](https://www.zaproxy.org/docs/alerts/100000/)



##### Informational (High)

### Description

A response code of 400 was returned by the server.
This may indicate that the application is failing to handle unexpected input correctly.
Raised by the 'Alert on HTTP Response Code Error' script

* URL: http://host.docker.internal:5067/api/Collections/10/items/10
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/10`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items/10/
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/10/`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/10
  * Node Name: `http://host.docker.internal:5067/api/quotes/10`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/10/
  * Node Name: `http://host.docker.internal:5067/api/quotes/10/`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/computeMetadata/v1/
  * Node Name: `http://host.docker.internal:5067/computeMetadata/v1/`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/latest/meta-data/
  * Node Name: `http://host.docker.internal:5067/latest/meta-data/`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/metadata/instance
  * Node Name: `http://host.docker.internal:5067/metadata/instance`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/metadata/v1
  * Node Name: `http://host.docker.internal:5067/metadata/v1`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/opc/v1/instance/
  * Node Name: `http://host.docker.internal:5067/opc/v1/instance/`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/opc/v2/instance/
  * Node Name: `http://host.docker.internal:5067/opc/v2/instance/`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/openstack/latest/meta_data.json
  * Node Name: `http://host.docker.internal:5067/openstack/latest/meta_data.json`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067
  * Node Name: `http://host.docker.internal:5067`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067
  * Node Name: `http://host.docker.internal:5067`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/
  * Node Name: `http://host.docker.internal:5067/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/2449693498322184795
  * Node Name: `http://host.docker.internal:5067/2449693498322184795`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api
  * Node Name: `http://host.docker.internal:5067/api`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api
  * Node Name: `http://host.docker.internal:5067/api`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/
  * Node Name: `http://host.docker.internal:5067/api/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/5193529667108898963
  * Node Name: `http://host.docker.internal:5067/api/5193529667108898963`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/
  * Node Name: `http://host.docker.internal:5067/api/Collections/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10
  * Node Name: `http://host.docker.internal:5067/api/Collections/10`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/963421305010439616
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/963421305010439616`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items/
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items/5601599676293406145
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/5601599676293406145`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `405`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items/actuator/health
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/actuator/health`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/703958508802478555
  * Node Name: `http://host.docker.internal:5067/api/Collections/703958508802478555`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/summaries%3FownerId=ownerId&previewSize=http%253A%252F%252Fwww.google.com%252F
  * Node Name: `http://host.docker.internal:5067/api/Collections/summaries (ownerId,previewSize)`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/summaries%3FownerId=7315377973781350176.owasp.org&previewSize=3
  * Node Name: `http://host.docker.internal:5067/api/Collections/summaries (ownerId,previewSize)`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/summaries-dapper%3FownerId=ownerId&previewSize=http%253A%252F%252Fwww.google.com%252F
  * Node Name: `http://host.docker.internal:5067/api/Collections/summaries-dapper (ownerId,previewSize)`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/summaries-dapper%3FownerId=7315377973781350176.owasp.org&previewSize=3
  * Node Name: `http://host.docker.internal:5067/api/Collections/summaries-dapper (ownerId,previewSize)`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/summaries-dapper/
  * Node Name: `http://host.docker.internal:5067/api/Collections/summaries-dapper/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/summaries/
  * Node Name: `http://host.docker.internal:5067/api/Collections/summaries/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth
  * Node Name: `http://host.docker.internal:5067/api/auth`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth
  * Node Name: `http://host.docker.internal:5067/api/auth`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/
  * Node Name: `http://host.docker.internal:5067/api/auth/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/3854619098194639397
  * Node Name: `http://host.docker.internal:5067/api/auth/3854619098194639397`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/me
  * Node Name: `http://host.docker.internal:5067/api/auth/me`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/me/
  * Node Name: `http://host.docker.internal:5067/api/auth/me/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/cache
  * Node Name: `http://host.docker.internal:5067/api/cache`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/cache
  * Node Name: `http://host.docker.internal:5067/api/cache`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/cache/
  * Node Name: `http://host.docker.internal:5067/api/cache/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/cache/6426492538715536479
  * Node Name: `http://host.docker.internal:5067/api/cache/6426492538715536479`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/cache/stats/
  * Node Name: `http://host.docker.internal:5067/api/cache/stats/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo
  * Node Name: `http://host.docker.internal:5067/api/demo`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo
  * Node Name: `http://host.docker.internal:5067/api/demo`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/
  * Node Name: `http://host.docker.internal:5067/api/demo/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/1274577576703461955
  * Node Name: `http://host.docker.internal:5067/api/demo/1274577576703461955`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/resilience/
  * Node Name: `http://host.docker.internal:5067/api/demo/resilience/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/profiling
  * Node Name: `http://host.docker.internal:5067/api/profiling`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/profiling
  * Node Name: `http://host.docker.internal:5067/api/profiling`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/profiling/
  * Node Name: `http://host.docker.internal:5067/api/profiling/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/profiling/7503388903468220035
  * Node Name: `http://host.docker.internal:5067/api/profiling/7503388903468220035`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/profiling/author-stats-fast/
  * Node Name: `http://host.docker.internal:5067/api/profiling/author-stats-fast/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/profiling/author-stats-slow/
  * Node Name: `http://host.docker.internal:5067/api/profiling/author-stats-slow/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes
  * Node Name: `http://host.docker.internal:5067/api/quotes`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes
  * Node Name: `http://host.docker.internal:5067/api/quotes`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes%3Fpage=10&size=10&author=author
  * Node Name: `http://host.docker.internal:5067/api/quotes (author,page,size)`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes%3Fpage=7315377973781350176.owasp.org&size=10&author=author
  * Node Name: `http://host.docker.internal:5067/api/quotes (author,page,size)`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/
  * Node Name: `http://host.docker.internal:5067/api/quotes/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/10
  * Node Name: `http://host.docker.internal:5067/api/quotes/10`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/10/
  * Node Name: `http://host.docker.internal:5067/api/quotes/10/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/7518072587589830894
  * Node Name: `http://host.docker.internal:5067/api/quotes/7518072587589830894`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience
  * Node Name: `http://host.docker.internal:5067/api/resilience`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience
  * Node Name: `http://host.docker.internal:5067/api/resilience`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/
  * Node Name: `http://host.docker.internal:5067/api/resilience/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/2990039879978241514
  * Node Name: `http://host.docker.internal:5067/api/resilience/2990039879978241514`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/call/
  * Node Name: `http://host.docker.internal:5067/api/resilience/call/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/stats/
  * Node Name: `http://host.docker.internal:5067/api/resilience/stats/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream
  * Node Name: `http://host.docker.internal:5067/api/upstream`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream
  * Node Name: `http://host.docker.internal:5067/api/upstream`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/
  * Node Name: `http://host.docker.internal:5067/api/upstream/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/9144039964067047742
  * Node Name: `http://host.docker.internal:5067/api/upstream/9144039964067047742`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode/
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode/7059029494757321401
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode/7059029494757321401`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `405`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/state/
  * Node Name: `http://host.docker.internal:5067/api/upstream/state/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/status/
  * Node Name: `http://host.docker.internal:5067/api/upstream/status/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/openapi
  * Node Name: `http://host.docker.internal:5067/openapi`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/openapi
  * Node Name: `http://host.docker.internal:5067/openapi`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/openapi/
  * Node Name: `http://host.docker.internal:5067/openapi/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/openapi/2736616764792827278
  * Node Name: `http://host.docker.internal:5067/openapi/2736616764792827278`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/openapi/v1.json/
  * Node Name: `http://host.docker.internal:5067/openapi/v1.json/`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections
  * Node Name: `http://host.docker.internal:5067/api/Collections ()({name,ownerId})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/
  * Node Name: `http://host.docker.internal:5067/api/Collections/ ()({name,ownerId})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items/10
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/10`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `404`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/Collections/10/items/10/
  * Node Name: `http://host.docker.internal:5067/api/Collections/10/items/10/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/login
  * Node Name: `http://host.docker.internal:5067/api/auth/login ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/login
  * Node Name: `http://host.docker.internal:5067/api/auth/login ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/login
  * Node Name: `http://host.docker.internal:5067/api/auth/login ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/login/
  * Node Name: `http://host.docker.internal:5067/api/auth/login/ ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/register
  * Node Name: `http://host.docker.internal:5067/api/auth/register ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/register
  * Node Name: `http://host.docker.internal:5067/api/auth/register ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/register/
  * Node Name: `http://host.docker.internal:5067/api/auth/register/ ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/cache/reset/
  * Node Name: `http://host.docker.internal:5067/api/cache/reset/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/queue-work%3FdelayMs=http%253A%252F%252Fwww.google.com%252F
  * Node Name: `http://host.docker.internal:5067/api/demo/queue-work (delayMs)`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/queue-work%3FdelayMs=5%253BURL%253D%2527https%253A%252F%252F7315377973781350176.owasp.org%2527
  * Node Name: `http://host.docker.internal:5067/api/demo/queue-work (delayMs)`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/queue-work/
  * Node Name: `http://host.docker.internal:5067/api/demo/queue-work/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes
  * Node Name: `http://host.docker.internal:5067/api/quotes ()({author,text})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes
  * Node Name: `http://host.docker.internal:5067/api/quotes ()({author,text})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/quotes/
  * Node Name: `http://host.docker.internal:5067/api/quotes/ ()({author,text})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/call%3FidempotencyKey=https%253A%252F%252F7315377973781350176.owasp.org
  * Node Name: `http://host.docker.internal:5067/api/resilience/call (idempotencyKey)`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/call/
  * Node Name: `http://host.docker.internal:5067/api/resilience/call/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/resilience/reset/
  * Node Name: `http://host.docker.internal:5067/api/resilience/reset/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode/mode%3FslowDelaySeconds=1.2
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode/mode (slowDelaySeconds)`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode/mode%3FslowDelaySeconds=5%253BURL%253D%2527https%253A%252F%252F7315377973781350176.owasp.org%2527
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode/mode (slowDelaySeconds)`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/mode/mode/
  * Node Name: `http://host.docker.internal:5067/api/upstream/mode/mode/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/reset/
  * Node Name: `http://host.docker.internal:5067/api/upstream/reset/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/upstream/submit/
  * Node Name: `http://host.docker.internal:5067/api/upstream/submit/`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `429`
  * Other Info: ``


Instances: 112

### Solution



### Reference



#### CWE Id: [ 388 ](https://cwe.mitre.org/data/definitions/388.html)


#### WASC Id: 20

#### Source ID: 4

### [ Authentication Request Identified ](https://www.zaproxy.org/docs/alerts/10111/)



##### Informational (High)

### Description

The given request has been identified as an authentication request. The 'Other Info' field contains a set of key=value lines which identify any relevant fields. If the request is in a context which has an Authentication Method set to "Auto-Detect" then this rule will change the authentication to match the request identified.

* URL: http://host.docker.internal:5067/api/auth/login
  * Node Name: `http://host.docker.internal:5067/api/auth/login ()({email,password})`
  * Method: `POST`
  * Parameter: `email`
  * Attack: ``
  * Evidence: `password`
  * Other Info: `userParam=email
userValue=zaproxy@example.com
passwordParam=password`


Instances: 1

### Solution

This is an informational alert rather than a vulnerability and so there is nothing to fix.

### Reference


* [ https://www.zaproxy.org/docs/desktop/addons/authentication-helper/auth-req-id/ ](https://www.zaproxy.org/docs/desktop/addons/authentication-helper/auth-req-id/)



#### Source ID: 3

### [ Non-Storable Content ](https://www.zaproxy.org/docs/alerts/10049/)



##### Informational (Medium)

### Description

The response contents are not storable by caching components such as proxy servers. If the response does not contain sensitive, personal or user-specific information, it may benefit from being stored and cached, to improve performance.

* URL: http://host.docker.internal:5067/api/quotes/10
  * Node Name: `http://host.docker.internal:5067/api/quotes/10`
  * Method: `DELETE`
  * Parameter: ``
  * Attack: ``
  * Evidence: `DELETE `
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/resilience
  * Node Name: `http://host.docker.internal:5067/api/demo/resilience`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: `503`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/login
  * Node Name: `http://host.docker.internal:5067/api/auth/login ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `401`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/auth/register
  * Node Name: `http://host.docker.internal:5067/api/auth/register ()({email,password})`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `400`
  * Other Info: ``
* URL: http://host.docker.internal:5067/api/demo/queue-work%3FdelayMs=10
  * Node Name: `http://host.docker.internal:5067/api/demo/queue-work (delayMs)`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: `202`
  * Other Info: ``

Instances: Systemic


### Solution

The content may be marked as storable by ensuring that the following conditions are satisfied:
The request method must be understood by the cache and defined as being cacheable ("GET", "HEAD", and "POST" are currently defined as cacheable)
The response status code must be understood by the cache (one of the 1XX, 2XX, 3XX, 4XX, or 5XX response classes are generally understood)
The "no-store" cache directive must not appear in the request or response header fields
For caching by "shared" caches such as "proxy" caches, the "private" response directive must not appear in the response
For caching by "shared" caches such as "proxy" caches, the "Authorization" header field must not appear in the request, unless the response explicitly allows it (using one of the "must-revalidate", "public", or "s-maxage" Cache-Control response directives)
In addition to the conditions above, at least one of the following conditions must also be satisfied by the response:
It must contain an "Expires" header field
It must contain a "max-age" response directive
For "shared" caches such as "proxy" caches, it must contain a "s-maxage" response directive
It must contain a "Cache Control Extension" that allows it to be cached
It must have a status code that is defined as cacheable by default (200, 203, 204, 206, 300, 301, 404, 405, 410, 414, 501).

### Reference


* [ https://datatracker.ietf.org/doc/html/rfc7234 ](https://datatracker.ietf.org/doc/html/rfc7234)
* [ https://datatracker.ietf.org/doc/html/rfc7231 ](https://datatracker.ietf.org/doc/html/rfc7231)
* [ https://www.w3.org/Protocols/rfc2616/rfc2616-sec13.html ](https://www.w3.org/Protocols/rfc2616/rfc2616-sec13.html)


#### CWE Id: [ 524 ](https://cwe.mitre.org/data/definitions/524.html)


#### WASC Id: 13

#### Source ID: 3

### [ Storable and Cacheable Content ](https://www.zaproxy.org/docs/alerts/10049/)



##### Informational (Medium)

### Description

The response contents are storable by caching components such as proxy servers, and may be retrieved directly from the cache, rather than from the origin server by the caching servers, in response to similar requests from other users. If the response data is sensitive, personal or user-specific, this may result in sensitive information being leaked. In some cases, this may even result in a user gaining complete control of the session of another user, depending on the configuration of the caching components in use in their environment. This is primarily an issue where "shared" caching servers such as "proxy" caches are configured on the local network. This configuration is typically found in corporate or educational environments, for instance.

* URL: http://host.docker.internal:5067/api/cache/stats
  * Node Name: `http://host.docker.internal:5067/api/cache/stats`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: ``
  * Other Info: `In the absence of an explicitly specified caching lifetime directive in the response, a liberal lifetime heuristic of 1 year was assumed. This is permitted by rfc7234.`
* URL: http://host.docker.internal:5067/api/profiling/author-stats-fast
  * Node Name: `http://host.docker.internal:5067/api/profiling/author-stats-fast`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: ``
  * Other Info: `In the absence of an explicitly specified caching lifetime directive in the response, a liberal lifetime heuristic of 1 year was assumed. This is permitted by rfc7234.`
* URL: http://host.docker.internal:5067/api/profiling/author-stats-slow
  * Node Name: `http://host.docker.internal:5067/api/profiling/author-stats-slow`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: ``
  * Other Info: `In the absence of an explicitly specified caching lifetime directive in the response, a liberal lifetime heuristic of 1 year was assumed. This is permitted by rfc7234.`
* URL: http://host.docker.internal:5067/openapi/v1.json
  * Node Name: `http://host.docker.internal:5067/openapi/v1.json`
  * Method: `GET`
  * Parameter: ``
  * Attack: ``
  * Evidence: ``
  * Other Info: `In the absence of an explicitly specified caching lifetime directive in the response, a liberal lifetime heuristic of 1 year was assumed. This is permitted by rfc7234.`
* URL: http://host.docker.internal:5067/api/cache/reset
  * Node Name: `http://host.docker.internal:5067/api/cache/reset`
  * Method: `POST`
  * Parameter: ``
  * Attack: ``
  * Evidence: ``
  * Other Info: `In the absence of an explicitly specified caching lifetime directive in the response, a liberal lifetime heuristic of 1 year was assumed. This is permitted by rfc7234.`

Instances: Systemic


### Solution

Validate that the response does not contain sensitive, personal or user-specific information. If it does, consider the use of the following HTTP response headers, to limit, or prevent the content being stored and retrieved from the cache by another user:
Cache-Control: no-cache, no-store, must-revalidate, private
Pragma: no-cache
Expires: 0
This configuration directs both HTTP 1.0 and HTTP 1.1 compliant caching servers to not store the response, and to not retrieve the response (without validation) from the cache, in response to a similar request.

### Reference


* [ https://datatracker.ietf.org/doc/html/rfc7234 ](https://datatracker.ietf.org/doc/html/rfc7234)
* [ https://datatracker.ietf.org/doc/html/rfc7231 ](https://datatracker.ietf.org/doc/html/rfc7231)
* [ https://www.w3.org/Protocols/rfc2616/rfc2616-sec13.html ](https://www.w3.org/Protocols/rfc2616/rfc2616-sec13.html)


#### CWE Id: [ 524 ](https://cwe.mitre.org/data/definitions/524.html)


#### WASC Id: 13

#### Source ID: 3



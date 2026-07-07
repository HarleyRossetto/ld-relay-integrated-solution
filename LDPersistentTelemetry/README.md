# LaunchDarkly Persistence Metrics

### The brief

The intent of the project is to provide a small side-car application for any Redis/Valkey instance used by a LaunchDarkly Relay Proxy for persistence.

The Relay Proxy exposes telemetry that highlights _who_ is using it for streaming and polling connections, this helps platform teams understand the usage of the Relay Proxy as a service. When utilising a Redis/Valkey cache for daemon mode or big segments LaunchDarkly SDK clients communicate directly with the cache; as a result there is no telemetry available to platform teams to understand the usage of the cache. This project aims to provide a solution which can expose metrics with an intrinsic understanding of how the cache is actually utilised.


The intention is to provide the following telemetry metrics:
- Users connected to the cache (including the number of connections)
- The number of keys per LaunchDarkly project + environment combo - important for understanding who is using how much of the cache. 
    - When hosting a cache centrally within an organisation where multiple teams are utilising a single cache instance it is important to know who is using how much of the cache. The relay proxy does not provide the ability to limit who can create big segments once their project has been whitelisted, so it is possible for one team to use a disproportionate amount of the cache. Without detailed ownership metrics it is difficult to understand who is using how much without exploring each project + environment segment combo or scraping the LaunchDarkly Audit log.

## Supported Metrics

A `user` is a Redis / Valkey ACL user.
A `client` is a individual Redis / Valkey connection from an application (in this case embedded LaunchDarkly SDK client) to the cache.


| Metric | Description | Unit |
|--------|-------------|------|
| `keys.usage` | Number of keys per environment prefix | N/A - A count |
| `keys.big_segment_membership_count` | Number of contexts in big segments per environment and segment kind | N/A - A count |
| `users.connected` | Number of connected clients per user | N/A - A count |
| `users.idle_seconds` | Number of seconds a connected client has been idle for | Seconds |
| `users.network_bytes_out` | Total networks bytes send per user | Bytes |
| `users.network_bytes_in` | Total networks bytes received per user | Bytes |


## Context Providers

To derive accurate key usage metrics, the applications needs to understand the context in which the Relay Proxy is operating. It requires information about how cache keys / prefixes map to LaunchDarkly proejcts and environments, and likewise segments.

There are two clear methods for dynamically obtaining this information:

1. Querying the Relay Proxy's `/status` endpoint - this provides a list of all configured projects and environments, allowing us to map client-side IDs (used to prefix cache keys) to the environment they belong to. This requires less configuration however it adds another dependency for this solution to work, this takes this application from being a side-car to a Valkey / Redis instance to being it's own service within a Relay Proxy deployment.

1. When the Relay Proxy is configured for Auto-config caching (via `AUTO_CONFIG_CACHE_KEY`) it will cache and encrypt it's configuration into the cache. The intended purpose of this feature is to allow the Relay Proxy to be capable of serving proxy requests in the event it has no network access to LaunchDarkly's servers. This application can leverage this feature to obtain it's configuration context from the cache itself. In order to do this this application needs to be configured with the same `AUTO_CONFIG_CACHE_KEY` and `AUTO_CONFIG_CACHE_KEY_ENCRYPTION_KEY` values as the Relay Proxy. This method is more complex to configure but allows this application to operate as a side-car to a Valkey / Redis instance without any additional dependencies.

## Scraping Methods

There are numerous ways to obtain the metrics from the Redis / Valkey instance, each with their own advantages and limitations. The concern for Redis / Valkey is the single threaded nature of the server, and the fact that it is a high performance cache. Any method which requires a large number of keys to be scanned or iterated over will have a negative impact on the performance of the cache, and as such should be minimised.

1. Method 1 - Calculating all metrics within the application itself. This is the simplest method to implement, however it requires performing at least two full scans of the cache to obtain all the metrics. This requires transferring all the keys from the cache to the application which quickly adds up in terms of network bandwidth. Likewise, it requires individual requests per big-segment key to obtain it's members (Segments the context is found in) - this incurs a large number of network round trips.

I've benchmarked this method, and with a few optimisations it can scrape all the metrics for 1 million keys in under 4 seconds. This was performed on a Linux host machines with a 9800X3D processor, Valkey running within a docker container and this application running on the host machine.

1. Method 2 - Calculating the metrics on the cache server itself. This method utilises Lua scripts to perform the calculations on the cache, returning the final accumulated metrics to the application. This splits the logic across small Lua scripts and the host application but minimises the volume of data transferred over the network and the number of round trips required to obtain the metrics. 

Big Segment key metrics still require individual requests to obtain members for a batch of keys - this is not strictly necessary, however it helps to reduce the amount of time the cache server spends calculating the metrics. Ideally each evaluation should consume less than 5 milliseconds of server time.

This method has been benchmarked and can scrape all the metrics for 1 million keys in under 2 seconds. This was performed on a Linux host machines with a 9800X3D processor, Valkey running within a docker container and this application running on the host machine.

### Notes:

Performance could be improved by using `github.com/microsoft/garnet` which is a Redis-compatible cache which support multi-threading, this could be tested in the future.

With large cache sizes the execution time of scraper will increase linearly, a cache with 30 million keys will take approximately 15 seconds to scrape all the metrics. It is important to note the total execution time and ensure the interval between scrapes is sufficient to avoid overlapping.
It is unlikely that regular scrapes are necessary, and one scrape every 5 minutes should be sufficient to provide a solid understanding of the state and usage of the cache. Larger cache sizes may require some artificial delays to be introduced into the scraping process to avoid overloading / consuming too much of the cache server's time.

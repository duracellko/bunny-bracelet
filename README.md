# Bunny Bracelet

- [Overview](#Overview)
- [Installation](#Installation)
- [How to use](#How-to-use)
- [Configuration](#Configuration)
- [Known limitations](#Known-limitations)

## Overview

Bunny Bracelet program relays messages between multiple [RabbitMQ](https://www.rabbitmq.com/) instances over HTTP protocol.

![Bunny Bracelet example setup](docs/images/example-setup.svg)

Previous example shows setup of 2 RabbitMQ instances and 2 Bunny Bracelet instances. Program `Bunny Bracelet A` consumes messages from `RabbitMQ A` and forwards them to `Bunny Bracelet B` over HTTP protocol. Then program `Bunny Bracelet B` publishes the messages to `RabbitMQ B`.

Single Bunny Bracelet program can relay messages to multiple instances or from multiple instances.

![Bunny Bracelet fork setup](docs/images/fork-setup.svg)

![Bunny Bracelet join setup](docs/images/join-setup.svg)

## Installation

Bunny Bracelet is natively supported on Linux (x64), Windows (x64) and any platform running [.NET 8.0](https://dotnet.microsoft.com/). Installation is simple. Download and unzip the program specific for your platform.

### Requirements

#### Ubuntu

- Ubuntu 22.04 (for older Ubuntu versions see Other platforms)

#### Windows

- All supported Windows versions. [Details](https://github.com/dotnet/core/blob/main/release-notes/8.0/supported-os.md#windows)

#### Other platforms

- Installed [ASP.NET Core 8.0 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

### Docker

Comming soon

## How to use

Check [Tutorial](docs/tutorial.md) to see Bunny Bracelet in action.

[RabbitMQ](https://www.rabbitmq.com/) is a message broker that accepts and forwards messages. One or more publishing applications may send messages to a RabbitMQ exchange. From the exchange the messages are distributed to one or more queues. Then the messages may be consumed by an _outbound_ Bunny Bracelet that forwards the messages to one or more _inbound_ Bunny Bracelets, also called _endpoints_. Each _inbound_ Bunny Bracelet then publishes the messages on another RabbitMQ exchange.

![Inbound and outbound Bunny Bracelets](docs/images/inbound-outbound.svg)

**Note**: Single Bunny Bracelet program can be in both roles _inbound_ and _outbound_.

### Outbound Bunny Bracelet

Outbound Bunny Bracelet consumes messages from RabbitMQ and forwards them to a remote Bunny Bracelet instance. The program must have configured RabbitMQ URI, outbound exchange and URL of a remote Bunny Bracelet that the messages should be forwarded to.

Following command runs Bunny Bracelet program that consumes messages published to exchange `my-outbound` on RabbitMQ `amqp://guest:guest@localhost` and forwards the messages to `http://inbound-bunny.remote`.

Linux:
```bash
./BunnyBracelet --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote
```

Windows:
```cmd
.\BunnyBracelet.exe --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote
```

The previous example forwards messages to single _endpoint_ (remote Bunny Bracelet) with index 0.

It is possible to specify a queue name that should be used to forward messages to an endpoint. And the queue can be configured to continue collecting messages, while Bunny Bracelet restarts. This is specified by setting `AutoDelete` value to `false`.

Linux:
```bash
./BunnyBracelet --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote --BunnyBracelet:Endpoints:0:Queue:Name outbound-queue --BunnyBracelet:Endpoints:0:Queue:AutoDelete false
```

Windows:
```cmd
.\BunnyBracelet.exe --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote --BunnyBracelet:Endpoints:0:Queue:Name outbound-queue --BunnyBracelet:Endpoints:0:Queue:AutoDelete false
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote --BunnyBracelet:Endpoints:0:Queue:Name outbound-queue --BunnyBracelet:Endpoints:0:Queue:AutoDelete false
```

And the following example presents how to configure outbound Bunny Bracelet forwarding messages to 2 endpoints (remote Bunny Bracelets): `http://inbound-bunny-1.remote` and `http://inbound-bunny-2.remote`.

Linux:
```bash
./BunnyBracelet --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny-1.remote --BunnyBracelet:Endpoints:1:Uri http://inbound-bunny-2.remote
```

Windows:
```cmd
.\BunnyBracelet.exe --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny-1.remote --BunnyBracelet:Endpoints:1:Uri http://inbound-bunny-2.remote
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny-1.remote --BunnyBracelet:Endpoints:1:Uri http://inbound-bunny-2.remote
```

When the receiving _inbound_ Bunny Bracelet requires authentication it is possible to specify a shared key that should be used to _sign_ the messages relayed by _outbound_ Bunny Bracelet. The key is encoded using [Base64](https://en.wikipedia.org/wiki/Base64). It is also required to specify, whether the key 1 or 2 should be used.

Linux:
```bash
./BunnyBracelet --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote --BunnyBracelet:Authentication:Key1 Vi8rjMaNa3D92SepImiJiUjJUt3kyUc70w0tT+7+ztTV5QKQqzEfWmHIe9/CSOVIsc/fjADbQ7+ueuqjW42vUw== --BunnyBracelet:Authentication:UseKeyIndex 1
```

Windows:
```cmd
.\BunnyBracelet.exe --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote --BunnyBracelet:Authentication:Key1 Vi8rjMaNa3D92SepImiJiUjJUt3kyUc70w0tT+7+ztTV5QKQqzEfWmHIe9/CSOVIsc/fjADbQ7+ueuqjW42vUw== --BunnyBracelet:Authentication:UseKeyIndex 1
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote --BunnyBracelet:Authentication:Key1 Vi8rjMaNa3D92SepImiJiUjJUt3kyUc70w0tT+7+ztTV5QKQqzEfWmHIe9/CSOVIsc/fjADbQ7+ueuqjW42vUw== --BunnyBracelet:Authentication:UseKeyIndex 1
```

### Inbound Bunny Bracelet

Inbound Bunny Bracelet receives messages from an outbound Bunny Bracelet on the specified network interface and port. Then all received messages are published to specified exchange on RabbitMQ.

Following command runs Bunny Bracelet program that receives messages on port 8080 and publishes them to exchange `my-inbound` on RabbitMQ `amqp://guest:guest@localhost`.

Linux:
```bash
./BunnyBracelet --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:InboundExchange:Name my-inbound
```

Windows:
```
.\BunnyBracelet.exe --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:InboundExchange:Name my-inbound
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:InboundExchange:Name my-inbound
```

And the following example presents running of Bunny Bracelet program in both roles as _inbound_ and _outbound_.

Linux:
```bash
./BunnyBracelet --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost:5672 --BunnyBracelet:InboundExchange:Name my-inbound --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote
```

Windows:
```cmd
.\BunnyBracelet.exe --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost:5672 --BunnyBracelet:InboundExchange:Name my-inbound --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost:5672 --BunnyBracelet:InboundExchange:Name my-inbound --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://inbound-bunny.remote
```

It is possible to configure message authentication. It means that only messages _signed_ by a shared key would be accepted by _inbound_ Bunny Bracelet. The key is encoded using [Base64](https://en.wikipedia.org/wiki/Base64).

Linux:
```bash
./BunnyBracelet --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:InboundExchange:Name my-inbound --BunnyBracelet:Authentication:Key1 Vi8rjMaNa3D92SepImiJiUjJUt3kyUc70w0tT+7+ztTV5QKQqzEfWmHIe9/CSOVIsc/fjADbQ7+ueuqjW42vUw==
```

Windows:
```
.\BunnyBracelet.exe --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:InboundExchange:Name my-inbound --BunnyBracelet:Authentication:Key1 Vi8rjMaNa3D92SepImiJiUjJUt3kyUc70w0tT+7+ztTV5QKQqzEfWmHIe9/CSOVIsc/fjADbQ7+ueuqjW42vUw==
```

.NET Runtime:
```bash
dotnet BunnyBracelet.dll --urls http://*:8080 --BunnyBracelet:RabbitMQUri amqp://guest:guest@localhost --BunnyBracelet:InboundExchange:Name my-inbound --BunnyBracelet:Authentication:Key1 Vi8rjMaNa3D92SepImiJiUjJUt3kyUc70w0tT+7+ztTV5QKQqzEfWmHIe9/CSOVIsc/fjADbQ7+ueuqjW42vUw==
```

## Configuration

Bunny Bracelet program can be configured by a configuration file `appsettings.json`, command-line arguments, environment variables, or combination of them. It is possible to specify following settings:

- **Urls**: URL that specifies the network interface and the port that the program uses to accept HTTP connections. Default value is `http://localhost:5000` that accepts only local connections on port 5000. Setting should be configured to value `http://*:8080` to accept connections from all networks on port 8080. More information about this setting can be found in [ASP.NET Core configuration](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/webapplication).
- **BunnyBracelet**
    - **RabbitMQUri**: URI of RabbitMQ service that the messages should be relayed from or to. URI should be in format `amqp://username:password@host:port/vhost`. More information can be found in [RabbitMQ URI specification](https://www.rabbitmq.com/docs/uri-spec). Default value is `amqp://localhost/`.
    - **InboundExchange**: Configuration of the RabbitMQ exchange to queue incoming relayed messages.
        - **Name**: Name of the inbound RabbitMQ exchange. When it is not specified, then incoming messages are rejected.
        - **Type**: Type of the RabbitMQ exchange. Possible values are `direct`, `topic`, `fanout`, `headers`. Default value is `fanout`.
        - **Durable**: A value indicating whether the exchange should be persisted and exist after restart of RabbitMQ. Default value is `false`.
        - **AutoDelete**: A value indicating whether the exchange should be automatically deleted after all queues are removed. Default value is `false`.
    - **OutboundExchange**: Configuration of the RabbitMQ exchange to queue outgoing messages for relaying.
        - **Name**: Name of the outbound RabbitMQ exchange. When it is not specified, then the program does not relay outgoing messages.
        - **Type**: Type of the RabbitMQ exchange. Possible values are `direct`, `topic`, `fanout`, `headers`. Default value is `fanout`.
        - **Durable**: A value indicating whether the exchange should be persisted and exist after restart of RabbitMQ. Default value is `false`.
        - **AutoDelete**: A value indicating whether the exchange should be automatically deleted after all queues are removed. Default value is `false`.
    - **Endpoints**: collection of endpoints that configure forwarding of messages from specified queue to a remote Bunny Bracelet.
        - **Uri**: URL of the remote Bunny Bracelet that the messages should be forwarded to. When the value is not specified, then the forwarding of messages is disabled.
        - **Queue**: Configuration of the RabbitMQ queue that is used to consume messages, which should be forwarded to the specified endpoint.
            - **Name**: Name of the RabbitMQ queue. When the value is not specified or it is empty, then a new queue with unique name is created.
            - **Durable**: A value indicating whether the queue should be persisted and exist after restart of RabbitMQ. Default value is `false`.
            - **AutoDelete**: A value indicating whether the queue should be automatically deleted after all consumers are removed. It means that the queue would be deleted after ending Bunny Bracelet program. Default value is `true`.
    - **Authentication**: Configuration of the authentication for communication between inbound and outbound Bunny Bracelet.
        - **Key1**: Shared authentication key number 1 used for authentication of messages sent from inbound to outbound Bunny Bracelet. Both inbound and outbound Bunny Bracelets must have configured the same shared key. The value is arbitrary sequence of bytes encoded in [Base64](https://en.wikipedia.org/wiki/Base64). Recommended key size is at least 64 bytes.
        - **Key2**: Shared authentication key number 2 used for authentication of messages sent from inbound to outbound Bunny Bracelet. Both inbound and outbound Bunny Bracelets must have configured the same shared key. The value is arbitrary sequence of bytes encoded in Base64. Recommended key size is at least 64 bytes.
        - **UseKeyIndex**: A number that specifies, which shared authentication key should be used by inbound Bunny Bracelet. Possible values are 1 or 2. Default value is empty and it means that no authentication would be provided.
    - **Timeout**: Time in milliseconds to wait for a remote Bunny Bracelet to respond. The timeout applies, when forwarding an outbound message to a remote Bunny Bracelet. Default value is 30000 (30 seconds).
    - **RequeueDelay**: The delay time in millisecond between relay failure and returning of the message back to the queue. The delay time prevents exhausting of CPU and network resources. Default value is 1000 (1 second).

### Configuration file

It is possible to configure Bunny Bracelet using configuration JSON file. The file should be named `appsettings.json` and it has to be in the same directory as the program. Content of the file may be following:

```json
{
    "Urls": "http://*:8080",
    "BunnyBracelet": {
        "RabbitMQUri": "amqp://username:password@rabbitmq-host",
        "InboundExchange": {
            "Name": "my-inbound-exchange",
            "Type": "headers",
            "Durable": true,
            "AutoDelete": false
        },
        "OutboundExchange": {
            "Name": "my-outbound-exchange",
            "Type": "fanout",
            "Durable": false,
            "AutoDelete": false
        },
        "Endpoints": [
            {
                "Uri": "http://bunny-bracelet-1.remote",
                "Queue": {
                    "Name": "my-inbound-queue",
                    "Durable": true,
                    "AutoDelete": false
                }
            },
            {
                "Uri": "http://bunny-bracelet-2.remote"
            }
        ],
        "Timeout": 15000,
        "RequeueDelay": 400
    }
}
```

### Command-line arguments

All configuration settings can be configured using command-line arguments. Each setting is specified as a key-value pair in format `--key value`. Key is full hierarchical name of the setting and the hierarchy separator is `:`. For example `--BunnyBracelet:InboundExchange:Name my-inbound-exchange`.

As **Endpoints** setting is collection of endpoints, index is included in the full hierarchical name. For example configuration of the first endpoint is `--BunnyBracelet:Endpoints:0:Uri http://bunny.remote`.

More information can be found in [ASP.NET Core command-line configuration](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/#command-line)

### Environment variables

Configuration using environment variables is similar to configuration using command-line arguments. Each setting is specified as seperate environment variable. Name of the environment variable is full hierarchical name of the setting and the hierarchy separator is `__`. For example `SET BunnyBracelet__InboundExchange__Name=my-inbound-exchange`.

As **Endpoints** setting is collection of endpoints, index is included in the full hierarchical name. For example configuration of the first endpoint is `SET BunnyBracelet__Endpoints__0__Uri=http://bunny.remote`.

More information can be found in [ASP.NET Core configuration using environment variables](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/#non-prefixed-environment-variables)

### Authentication

It is possible to configure _inbound_ Bunny Bracelet to accept a message, only if it is authenticated using shared key and [HMAC-SHA256](https://en.wikipedia.org/wiki/HMAC) authentication code. The same key must be configured for _outbound_ and _inbound_ Bunny Bracelet. There can be 2 keys configured: `BunnyBracelet:Authentication:Key1` and `BunnyBracelet:Authentication:Key2`. The configured values are arbitrary byte sequences encoded in [Base64](https://en.wikipedia.org/wiki/Base64). Recommended key length is at least 64 bytes.

Inbound Bunny Bracelet accepts both keys, if 2 keys are configured. It is possible to configure only single key (either `Key1` or `Key2`), but it is not recommended. The outbound Bunny Bracelet must specify, which key should be used for authentication. `Key1` can be selected by configuration `BunnyBracelet:Authentication:UseKeyIndex=1` and `Key2` can be selected by configuration `BunnyBracelet:Authentication:UseKeyIndex=2`.

The program uses 2 shared authentication keys for easier key rotation. When your application uses `Key1` for authentication (by specifying `BunnyBracelet:Authentication:UseKeyIndex=1`) and would like to rotate the keys, it is not recommended to simply change the keys to the new values. It is very likely that there can be some delay or time difference between depoyment of inbound and outbound Bunny Bracelet. So the 2 programs would use different authentication keys for some time. This would lead to failed authentication, and thus lost of messages. Therefore, it is recommended to change the configuration in 2 steps:

1. Change inbound Bunny Bracelet to use `Key2` by setting `BunnyBracelet:Authentication:UseKeyIndex=2`.
2. Then change `Key1` configuration to a new value.

Then it is possible to rotate `Key2` in similar way.

## Known limitations

[Known limitations](docs/known-limitations.md)

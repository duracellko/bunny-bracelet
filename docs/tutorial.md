# Bunny Bracelet tutorial

In this tutorial we will setup 2 instances of RabbitMQ. Then we will publish a message in the first RabbitMQ instance and check that it will be in queue of the second RabbitMQ instance.

## Requirements

This tutorial requires that you have downloaded and unzipped Bunny Bracelet program and that you have Docker installed. The downloaded Bunny Bracelet program should be the correct one for your platform. If you use the one that requires .NET runtime, then you should have installed [ASP.NET Core 8.0 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

## RabbitMQ setup

First step is to setup 2 RabbitMQ instances. We will name them `rabbit-inbound` and `rabbit-outbound`. The simplest way is to run RabbitMQ in Docker container.

Command to run inbound RabbitMQ:

```
docker run --name rabbit-inbound -d -p 5672:5672 -p 15672:15672 -e RABBITMQ_DEFAULT_USER=bunny -e RABBITMQ_DEFAULT_PASS=P2ssw0rd1 rabbitmq:3-management
```

The container has exposed port 5672 for AMQP connection and 15672 for management UI. Management UI will be used to publish a message later. The container has also setup RabbitMQ username and password.

We can check that the RabbitMQ is running by opening browser and entering URL [http://localhost:15672](http://localhost:15672). We should see web page for entering username and password.

![RabbitMQ login page](images/tutorial/RabbitMQ-Login.png)

We should enter username _bunny_ and password _P2ssw0rd1_. After logging into RabbitMQ the management overview page should be opened.

![RabbitMQ management overview page](images/tutorial/RabbitMQ-Overview.png)

Then command to run outbound RabbitMQ:

```
docker run --name rabbit-outbound -d -p 5673:5672 -p 15673:15672 -e RABBITMQ_DEFAULT_USER=bunny -e RABBITMQ_DEFAULT_PASS=P2ssw0rd2 rabbitmq:3-management
```

Notice that it uses different ports and password.

We can open the management page the same way, but entering URL [http://localhost:15673](http://localhost:15673) and password _P2ssw0rd2_.

## Bunny Bracelet setup

At first we will start _outbound_ Bunny Bracelet that should consume messages from `rabbit-outbound` RabbitMQ. We can start the program using following command.

Linux
```bash
./BunnyBracelet --urls http://localhost:8081 --BunnyBracelet:RabbitMQUri amqp://bunny:P2ssw0rd1@localhost:5672 --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://localhost:8080
```

Windows
```cmd
.\BunnyBracelet.exe --urls http://localhost:8081 --BunnyBracelet:RabbitMQUri amqp://bunny:P2ssw0rd1@localhost:5672 --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://localhost:8080
```

.NET Runtime
```bash
dotnet BunnyBracelet.dll --urls http://localhost:8081 --BunnyBracelet:RabbitMQUri amqp://bunny:P2ssw0rd1@localhost:5672 --BunnyBracelet:OutboundExchange:Name my-outbound --BunnyBracelet:Endpoints:0:Uri http://localhost:8080
```

The previous command configures Bunny Bracelet to forward the messages to another Bunny Bracelet listening at URL `http://localhost:8080`. That would be our _inbound_ Bunny Bracelet. Let's get it started, while the _inbound_ is still running. We can use following command:

Linux
```bash
./BunnyBracelet --urls http://localhost:8080 --BunnyBracelet:RabbitMQUri amqp://bunny:P2ssw0rd2@localhost:5673 --BunnyBracelet:InboundExchange:Name my-inbound
```

Windows
```cmd
.\BunnyBracelet.exe --urls http://localhost:8080 --BunnyBracelet:RabbitMQUri amqp://bunny:P2ssw0rd2@localhost:5673 --BunnyBracelet:InboundExchange:Name my-inbound
```

.NET Runtime
```bash
dotnet BunnyBracelet.dll --urls http://localhost:8080 --BunnyBracelet:RabbitMQUri amqp://bunny:P2ssw0rd2@localhost:5673 --BunnyBracelet:InboundExchange:Name my-inbound
```

## Setup receiving queue

Let's get back to _inbound_ RabbitMQ management page at [http://localhost:15673](http://localhost:15673). Select **Exchanges** tab.

![Inbound RabbitMQ exchange list](images/tutorial/Inbound-Exchange-list.png)

Click **Add a new exchange** and enter following values:

- **Name**: my-inbound
- **Type**: fanout
- **Durability**: Transient
- **Auto delete**: No
- **Internal**: No

![Add inbound exchange](images/tutorial/Add-Inbound-Exchange.png)

And click **Add exchange**.

Now we should create a queue. Open **Queues and Streams** tab. There shouldn't be any queues yet.

![Empty inbound RabbitMQ queue list](images/tutorial/Empty-Inbound-Queue-list.png)

Click **Add a new queue** and enter following values:

- **Name**: my-inbound-queue
- **Durability**: Transient

All other values can stay default.

![Add inbound queue](images/tutorial/Add-Inbound-Queue.png)

Click **Add queue**. The queue should be added to the list.

![Inbound queue list](images/tutorial/Inbound-Queue-list.png)

Open the queue from the list. Queue dashboard should be opened.

![Inbound queue page](images/tutorial/Inbound-Queue-page.png)

Open **Bindings** and add new Binding with following values:

- **From exchange**: my-inbound
- Keep **Routing key** empty.

![Add queue binding](images/tutorial/Add-Queue-binding.png)

Click **Bind**.

## Relaying message

Now everything is setup to send a message from _inbound_ RabbitMQ to _outbound_ RabbitMQ. Open management page of _inbound_ RabbitMQ at [http://localhost:15672](http://localhost:15672). Open **Exchanges** tab. There should be `my-outbound` exchange in the list.

![Outbound RabbitMQ exchange list](images/tutorial/Outbound-Exchange-list.png)

Click on the `my-outbound` exchange and open the exchange page.

![Outbound exchange page](images/tutorial/Outbound-Exchange-page.png)

Click **Publish message** and enter **Payload** "Hello, World!". Other values can stay empty.

![Publish message](images/tutorial/Publish-message.png)

Click **Publish message** button.

Now we should go back to _outbound_ RabbitMQ management page at [http://localhost:15673/#/queues/%2F/my-inbound-queue](http://localhost:15673/#/queues/%2F/my-inbound-queue). We should still have opened our `my-inbound-queue` queue page.

Now open section **Get messages** and then click on the button **Get Message(s)**. We should see the received message "Hello, World!".

![Received message](images/tutorial/Received-message.png)

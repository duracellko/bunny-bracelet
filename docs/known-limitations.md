# Known limitations

When using Bunny Bracelet you should be aware of certain limitations or unsupported scenarios.

## Inbound publishing durability

Inbound Bunny Bracelet considers the message relayed as soon as it is sent to the network. Current implementation of inbound Bunny Bracelet does not wait for acknowledgement by RabbitMQ. It means that the message can get lost, when a network connection to RabbitMQ is lost during publishing, or RabbitMQ is stopped before the message is written to persistent storage in case of durable queues.

## Fetching only 1 message at time

Outbound Bunny Bracelet fetches only single message at time. Advantage of this solution is that, when relaying of a message fails, then the message is sent back to queue and the order of the messages in the queue is not changed. It means that the end-to-end message delivery between multiple RabbitMQ instances is FIFO and it is ensured that the messages are consumed in the same order as they were published. However, disadvantage of this solution is lower throughput, especially when network latency between RabbitMQ and Bunny Bracelet is high.

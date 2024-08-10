workspace {
    model {
        ss = softwareSystem "Example" {
            r1 = container "RabbitMQ A" {
                tags "RabbitMQ"
            }
            r2 = container "RabbitMQ B" {
                tags "RabbitMQ"
            }
            b1 = container "Bunny Bracelet A" {
                tags "BunnyBracelet"
            }
            b2 = container "Bunny Bracelet B" {
                tags "BunnyBracelet"
            }
        }

        r1 -> b1 "AMPQ"
        b1 -> b2 "HTTP"
        b2 -> r2 "AMPQ"

        fss = softwareSystem "Fork_Example" {
            fr1 = container "RabbitMQ A" {
                tags "RabbitMQ"
            }
            fr2 = container "RabbitMQ B" {
                tags "RabbitMQ"
            }
            fr3 = container "RabbitMQ C" {
                tags "RabbitMQ"
            }
            fb1 = container "Bunny Bracelet A" {
                tags "BunnyBracelet"
            }
            fb2 = container "Bunny Bracelet B" {
                tags "BunnyBracelet"
            }
            fb3 = container "Bunny Bracelet C" {
                tags "BunnyBracelet"
            }
        }

        fr1 -> fb1 "AMPQ"
        fb1 -> fb2 "HTTP"
        fb1 -> fb3 "HTTP"
        fb2 -> fr2 "AMPQ"
        fb3 -> fr3 "AMPQ"

        jss = softwareSystem "Join_Example" {
            jr1 = container "RabbitMQ A" {
                tags "RabbitMQ"
            }
            jr2 = container "RabbitMQ B" {
                tags "RabbitMQ"
            }
            jr3 = container "RabbitMQ C" {
                tags "RabbitMQ"
            }
            jb1 = container "Bunny Bracelet A" {
                tags "BunnyBracelet"
            }
            jb2 = container "Bunny Bracelet B" {
                tags "BunnyBracelet"
            }
            jb3 = container "Bunny Bracelet C" {
                tags "BunnyBracelet"
            }
        }

        jr1 -> jb1 "AMPQ"
        jr2 -> jb2 "AMPQ"
        jb1 -> jb3 "HTTP"
        jb2 -> jb3 "HTTP"
        jb3 -> jr3 "AMPQ"

        ioss = softwareSystem "Inbound_Outbound" {
            ioro = container "Outbound RabbitMQ" {
                tags "RabbitMQ"
                ioroe = component "Outbound Exchange" {
                }
                ioroq = component "Outbound Queue" {
                }
            }
            iori = container "Inbound RabbitMQ" {
                tags "RabbitMQ"
                iorie = component "Inbound Exchange" {
                }
            }
            iobo = container "Outbound Bunny Bracelet" {
                tags "BunnyBracelet"
            }
            iobi = container "Inbound Bunny Bracelet" {
                tags "BunnyBracelet"
            }
        }

        ioroe -> ioroq "message"
        ioroq -> iobo "AMPQ"
        iobo -> iobi "HTTP"
        iobi -> iorie "AMPQ"
    }
    views {
        container ss "Example" {
            include *
            autolayout lr
        }
        container fss "Fork_Example" {
            include *
            autolayout lr
        }
        container jss "Join_Example" {
            include *
            autolayout lr
        }

        component ioro "Inbound_Outbound" {
            include iobo iobi
            include ioroe ioroq iorie
            autolayout lr
        }

        theme default
        styles {
            element "RabbitMQ" {
                background #ff6600
            }
        }
    }
}
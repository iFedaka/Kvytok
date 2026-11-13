workspace "Kvytok" "Seat reservation for events with a fixed number of seats." {

    model {
        buyer = person "Buyer" "Books a seat, from the seat-grid page or straight against the API."
        organiser = person "Organiser" "Creates events and their seats."
        paymentProvider = softwareSystem "Payment provider" "Confirms payments by calling a webhook; retries on failure, and may deliver twice." "External"

        kvytok = softwareSystem "Kvytok" "Reserves seats so that exactly one booking wins a contested seat, and confirms or expires them asynchronously." {
            api = container "Api" "Events, seat reservations, payment webhook, seat-grid demo page, OpenAPI reference. Relays outbox messages the in-process send missed." "ASP.NET Core minimal APIs, Azure Container Apps"
            worker = container "Worker" "Confirms paid reservations, expires unpaid ones, retries failed steps with backoff. Scales on queue length." ".NET BackgroundService, Azure Container Apps"
            migrator = container "Migrator" "Applies EF Core migrations and narrows the Api's and Worker's database roles to reading and writing rows. Run after each deploy." ".NET, Container Apps job"
            database = container "Database" "Events, seats, reservations, transactional outbox, processed message ids." "Azure SQL (serverless)" "Database"
            queue = container "Reservations queue" "Reservation created, payment received, expiry due (scheduled); dead-letter queue after five attempts." "Azure Service Bus" "Queue"
            insights = container "Application Insights" "Traces, metrics and logs; one trace from the Api across the queue to the Worker." "Azure Monitor" "Telemetry"
        }

        buyer -> api "Reserves seats, reads events and reservations" "HTTPS/JSON"
        organiser -> api "Creates events" "HTTPS/JSON"
        paymentProvider -> api "Reports payments" "HTTPS webhook"

        api -> database "Reads and writes events, seats, reservations and outbox, in one transaction" "EF Core, Entra ID"
        api -> queue "Publishes reservation created and expiry due; payment received" "AMQP, Entra ID"
        queue -> worker "Delivers messages" "AMQP, Entra ID"
        worker -> database "Confirms or expires reservations, records processed messages" "EF Core, Entra ID"
        migrator -> database "Migrates schema, narrows roles" "EF Core"

        api -> insights "Sends telemetry" "OpenTelemetry"
        worker -> insights "Sends telemetry" "OpenTelemetry"
        migrator -> insights "Sends telemetry" "OpenTelemetry"
    }

    views {
        systemContext kvytok "SystemContext" {
            include *
            autolayout tb
        }

        container kvytok "Containers" {
            include *
            autolayout tb
        }

        styles {
            element "Person" {
                shape Person
                background #08427b
                color #ffffff
            }
            element "Software System" {
                background #1168bd
                color #ffffff
            }
            element "External" {
                background #999999
            }
            element "Container" {
                background #438dd5
                color #ffffff
            }
            element "Database" {
                shape Cylinder
            }
            element "Queue" {
                shape Pipe
            }
        }
    }
}

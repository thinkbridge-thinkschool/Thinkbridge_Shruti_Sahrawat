// =============================================================================
// Shared types.
//
// `param subscriptions array` accepts anything: a subscription entry missing
// maxDeliveryCount, a lock duration typed as a string, a filter key spelled
// `filter` instead of `sqlFilter`. All of those compile, all of them pass
// `what-if`, and all of them fail partway through a deployment that has already
// created half the stack - with an ARM error naming a property path rather than
// the line in the parameter file that got it wrong.
//
// Declaring the shape once, here, moves every one of those to `bicep build`.
// =============================================================================

@export()
@description('One subscription on the quote-events topic.')
type topicSubscription = {
  @description('Subscription name, e.g. search-indexer.')
  @minLength(1)
  @maxLength(50)
  name: string

  @description('SQL filter expression matched against the message\'s application properties, e.g. "eventType = \'QuoteCreated\'". Empty means no filter: the auto-created $Default TrueFilter is left in place and the subscription receives everything.')
  sqlFilter: string

  @description('Deliveries before the broker dead-letters the message itself.')
  @minValue(1)
  @maxValue(2000)
  maxDeliveryCount: int

  @description('Peek-lock duration. Service Bus caps this at 300 seconds - a handler that needs longer has to renew the lock, not ask for a bigger one.')
  @minValue(30)
  @maxValue(300)
  lockDurationSeconds: int
}

@export()
@description('Azure SQL database SKU. `family` is optional because Basic and Standard do not take one and General Purpose requires it.')
type sqlSku = {
  name: string
  tier: string
  family: string?
  capacity: int
}

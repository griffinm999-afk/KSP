import { sqliteTable, text, integer } from 'drizzle-orm/sqlite-core';
export const colonies = sqliteTable('colonies', {
  id: text('id').primaryKey(),
  name: text('name').notNull(),
  body: text('body').notNull(),
  location: text('location').notNull(),
  createdAt: text('created_at').notNull()
});
export const buildings = sqliteTable('colony_buildings', {
  id: text('id').primaryKey(),
  colonyId: text('colony_id').notNull().references(()=>colonies.id),
  name: text('name').notNull()
});
export const liveFeed = sqliteTable('live_feed', {
  id: text('id').primaryKey(),
  frame: text('frame').notNull(),
  receivedAt: text('received_at').notNull(),
  demandUntil: text('demand_until').notNull()
});
export const wolfCommands = sqliteTable('wolf_commands', {
  id: text('id').primaryKey(),
  request: text('request').notNull(),
  status: text('status').notNull(),
  createdAt: text('created_at').notNull(),
  result: text('result')
});

export const observedBuildings = sqliteTable('observed_buildings', {
  id: text('id').primaryKey(),
  record: text('record').notNull(),
  updatedAt: text('updated_at').notNull(),
  revision: text('revision').notNull(),
  lifecycleState: text('lifecycle_state').notNull().default('active'),
  lifecycleChangedAt: text('lifecycle_changed_at'),
  lifecycleReason: text('lifecycle_reason'),
  lifecycleContext: text('lifecycle_context')
});

export const vesselCensusContexts = sqliteTable('vessel_census_contexts', {
  contextKey: text('context_key').primaryKey(),
  worldId: text('world_id').notNull(),
  sessionId: text('session_id').notNull(),
  loadEpoch: text('load_epoch').notNull(),
  scene: text('scene').notNull(),
  highestSequence: integer('highest_sequence').notNull(),
  status: text('status').notNull(),
  receivedAt: text('received_at').notNull(),
  observedUt: text('observed_ut').notNull()
});
export const buildingLifecycleEvents = sqliteTable('building_lifecycle_events', {
  id: text('id').primaryKey(),
  vesselId: text('vessel_id').notNull(),
  record: text('record').notNull(),
  createdAt: text('created_at').notNull()
});

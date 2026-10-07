CREATE TABLE `building_lifecycle_events` (
	`id` text PRIMARY KEY NOT NULL,
	`vessel_id` text NOT NULL,
	`record` text NOT NULL,
	`created_at` text NOT NULL
);
--> statement-breakpoint
CREATE TABLE `vessel_census_contexts` (
	`context_key` text PRIMARY KEY NOT NULL,
	`world_id` text NOT NULL,
	`session_id` text NOT NULL,
	`load_epoch` text NOT NULL,
	`scene` text NOT NULL,
	`highest_sequence` integer NOT NULL,
	`status` text NOT NULL,
	`received_at` text NOT NULL,
	`observed_ut` text NOT NULL
);
--> statement-breakpoint
ALTER TABLE `observed_buildings` ADD `lifecycle_state` text DEFAULT 'active' NOT NULL;--> statement-breakpoint
ALTER TABLE `observed_buildings` ADD `lifecycle_changed_at` text;--> statement-breakpoint
ALTER TABLE `observed_buildings` ADD `lifecycle_reason` text;--> statement-breakpoint
ALTER TABLE `observed_buildings` ADD `lifecycle_context` text;
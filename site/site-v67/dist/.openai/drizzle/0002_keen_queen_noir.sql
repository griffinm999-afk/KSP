CREATE TABLE `live_feed` (
	`id` text PRIMARY KEY NOT NULL,
	`frame` text NOT NULL,
	`received_at` text NOT NULL,
	`demand_until` text NOT NULL
);

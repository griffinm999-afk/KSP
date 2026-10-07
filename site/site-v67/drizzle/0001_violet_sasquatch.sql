CREATE TABLE `colony_buildings` (
	`id` text PRIMARY KEY NOT NULL,
	`colony_id` text NOT NULL,
	`name` text NOT NULL,
	FOREIGN KEY (`colony_id`) REFERENCES `colonies`(`id`) ON UPDATE no action ON DELETE no action
);

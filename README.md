### Introduction
This is a products synchronizer app between MySQL and WooCommerce written in C# and using WPF.
 
![Sync](ensonetsync.png?raw=true)

It runs periodic updates in a worker thread, downloads data from MySQL, uploads products and images to WooCommerce with a library through REST API, displays all events in a logging view (and also writing it in file), and stores data in its runtime cache.

First it downloads all the products from the webshop and stores them in the cache. Then it retrieves the current products data from the database and compare it to the cache. Differences are collected (new and changed products), then they get sent to the webshop. On successful upload, every changes made are also applied to the cache, so it can be used again next time, making updates faster.

Currently only stock quantities and prices data get updated, new procuts details and images created with two-way linking to the product.

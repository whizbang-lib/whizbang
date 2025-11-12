-- BFF Read Models Schema
-- This schema contains denormalized views optimized for UI queries

-- Create BFF schema for read models
CREATE SCHEMA IF NOT EXISTS bff;

-- Order read model (denormalized for fast queries)
CREATE TABLE IF NOT EXISTS bff.orders (
  order_id VARCHAR(50) PRIMARY KEY,
  customer_id VARCHAR(50) NOT NULL,
  tenant_id VARCHAR(50),  -- For future multi-tenancy
  status VARCHAR(50) NOT NULL,
  total_amount DECIMAL(18,2) NOT NULL,
  created_at TIMESTAMPTZ NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL,
  line_items JSONB NOT NULL,

  -- Additional denormalized fields for common queries
  item_count INT NOT NULL DEFAULT 0,
  payment_status VARCHAR(50),
  shipment_id VARCHAR(50),
  tracking_number VARCHAR(100)
);

-- Indexes for common queries
CREATE INDEX IF NOT EXISTS idx_orders_customer ON bff.orders(customer_id);
CREATE INDEX IF NOT EXISTS idx_orders_tenant ON bff.orders(tenant_id);
CREATE INDEX IF NOT EXISTS idx_orders_status ON bff.orders(status);
CREATE INDEX IF NOT EXISTS idx_orders_created_at ON bff.orders(created_at DESC);

-- Order status history (timeline of events)
CREATE TABLE IF NOT EXISTS bff.order_status_history (
  id SERIAL PRIMARY KEY,
  order_id VARCHAR(50) NOT NULL,
  status VARCHAR(50) NOT NULL,
  event_type VARCHAR(100) NOT NULL,
  timestamp TIMESTAMPTZ NOT NULL,
  details JSONB,

  CONSTRAINT fk_order
    FOREIGN KEY (order_id)
    REFERENCES bff.orders(order_id)
    ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_status_history_order ON bff.order_status_history(order_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_status_history_event_type ON bff.order_status_history(event_type);

-- Comments for documentation
COMMENT ON SCHEMA bff IS 'Backend-for-Frontend read models optimized for UI queries';
COMMENT ON TABLE bff.orders IS 'Denormalized order view maintained by BFF perspectives';
COMMENT ON TABLE bff.order_status_history IS 'Complete timeline of order status changes for tracking UI';

-- Sample view for admin queries (showing how to extend read models)
CREATE OR REPLACE VIEW bff.recent_orders AS
SELECT
  order_id,
  customer_id,
  status,
  total_amount,
  created_at,
  updated_at
FROM bff.orders
ORDER BY created_at DESC
LIMIT 100;

COMMENT ON VIEW bff.recent_orders IS 'Most recent 100 orders for admin dashboard';

-- =============================================================================
-- NEXORA — V099 — Index des clés étrangères réellement interrogées (analyse exhaustive 2026-10, §5.3)
--
-- L'analyse comptait 690 clés étrangères sans index commençant par leurs colonnes. En tenant compte des index
-- (company_id, x) et de l'usage, il en reste 291 non couvertes, surtout vers des données de référence (devise, compte,
-- taxe, utilisateur) jamais parcourues « du parent vers les enfants ». Seules les recherches réelles sont indexées :
-- clôture de caisse, trésorerie par compte, traçabilité des lots (rappel), stock par emplacement, liens d'achat
-- (compte 4081, rapprochement), contrepassations, recouvrement, historique d'un article.
-- Idempotente.
-- =============================================================================
CREATE INDEX IF NOT EXISTS payment_cash_session_id_fk_idx ON nexora.payment (cash_session_id) WHERE cash_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_return_cash_session_id_fk_idx ON nexora.sale_return (cash_session_id) WHERE cash_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_return_request_cash_session_id_fk_idx ON nexora.sale_return_request (cash_session_id) WHERE cash_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS customer_refund_cash_session_id_fk_idx ON nexora.customer_refund (cash_session_id) WHERE cash_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS employee_advance_cash_session_id_fk_idx ON nexora.employee_advance (cash_session_id) WHERE cash_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS salary_payment_cash_session_id_fk_idx ON nexora.salary_payment (cash_session_id) WHERE cash_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS payment_financial_account_id_fk_idx ON nexora.payment (financial_account_id) WHERE financial_account_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS expense_financial_account_id_fk_idx ON nexora.expense (financial_account_id) WHERE financial_account_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS financial_transaction_counterparty_account_id_fk_idx ON nexora.financial_transaction (counterparty_account_id) WHERE counterparty_account_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS bank_statement_line_matched_transaction_id_fk_idx ON nexora.bank_statement_line (matched_transaction_id) WHERE matched_transaction_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS stock_movement_lot_id_fk_idx ON nexora.stock_movement (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_line_lot_id_fk_idx ON nexora.sale_line (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS goods_receipt_line_lot_id_fk_idx ON nexora.goods_receipt_line (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_return_line_lot_id_fk_idx ON nexora.sale_return_line (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS stock_adjustment_line_lot_id_fk_idx ON nexora.stock_adjustment_line (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS stock_transfer_line_lot_id_fk_idx ON nexora.stock_transfer_line (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS inventory_count_line_lot_id_fk_idx ON nexora.inventory_count_line (lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS stock_movement_inventory_location_id_fk_idx ON nexora.stock_movement (inventory_location_id) WHERE inventory_location_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_invoice_goods_receipt_id_fk_idx ON nexora.supplier_invoice (goods_receipt_id) WHERE goods_receipt_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_invoice_purchase_order_id_fk_idx ON nexora.supplier_invoice (purchase_order_id) WHERE purchase_order_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_return_goods_receipt_id_fk_idx ON nexora.supplier_return (goods_receipt_id) WHERE goods_receipt_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_return_supplier_invoice_id_fk_idx ON nexora.supplier_return (supplier_invoice_id) WHERE supplier_invoice_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_return_supplier_id_fk_idx ON nexora.supplier_return (supplier_id) WHERE supplier_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS journal_entry_reversal_of_entry_id_fk_idx ON nexora.journal_entry (reversal_of_entry_id) WHERE reversal_of_entry_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS journal_entry_line_cost_center_id_fk_idx ON nexora.journal_entry_line (cost_center_id) WHERE cost_center_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS collection_action_receivable_id_fk_idx ON nexora.collection_action (receivable_id) WHERE receivable_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS customer_expense_receivable_id_fk_idx ON nexora.customer_expense (receivable_id) WHERE receivable_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS receivable_write_off_receivable_id_fk_idx ON nexora.receivable_write_off (receivable_id) WHERE receivable_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS payment_intent_sale_id_fk_idx ON nexora.payment_intent (sale_id) WHERE sale_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS payment_intent_payment_id_fk_idx ON nexora.payment_intent (payment_id) WHERE payment_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS delivery_sale_id_fk_idx ON nexora.delivery (sale_id) WHERE sale_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS delivery_stop_sales_order_id_fk_idx ON nexora.delivery_stop (sales_order_id) WHERE sales_order_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_line_modifier_sale_id_fk_idx ON nexora.sale_line_modifier (sale_id) WHERE sale_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_promotion_promotion_id_fk_idx ON nexora.sale_promotion (promotion_id) WHERE promotion_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_promotion_coupon_id_fk_idx ON nexora.sale_promotion (coupon_id) WHERE coupon_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_return_request_sale_return_id_fk_idx ON nexora.sale_return_request (sale_return_id) WHERE sale_return_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sale_return_line_product_id_fk_idx ON nexora.sale_return_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_invoice_line_product_id_fk_idx ON nexora.supplier_invoice_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS supplier_return_line_product_id_fk_idx ON nexora.supplier_return_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS inventory_count_line_product_id_fk_idx ON nexora.inventory_count_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS stock_adjustment_line_product_id_fk_idx ON nexora.stock_adjustment_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS stock_transfer_line_product_id_fk_idx ON nexora.stock_transfer_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sales_order_line_product_id_fk_idx ON nexora.sales_order_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS quote_line_product_id_fk_idx ON nexora.quote_line (product_id) WHERE product_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS purchase_request_line_product_id_fk_idx ON nexora.purchase_request_line (product_id) WHERE product_id IS NOT NULL;

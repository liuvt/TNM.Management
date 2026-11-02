"""Static regression guard for catalog navigation, table pagination, export and CRUD.
This is not a substitute for `dotnet build` or browser testing.
"""
from pathlib import Path

root=Path(__file__).resolve().parents[1]
layout=(root/'Components/Layout/MainLayout.razor').read_text()
input_page=(root/'Components/Pages/InputMaster.razor').read_text()
products=(root/'Components/Pages/Products.razor').read_text()
backend=(root/'Services/InputCatalog.cs').read_text()
script=(root/'wwwroot/js/catalog-export.js').read_text()
app=(root/'Components/App.razor').read_text()
sections=[
 'product-groups', 'supplier-groups','suppliers','customer-groups',
 'customers','areas','warehouses','buy-prices','sale-prices','campaigns']
for section in sections:
    assert f'/catalog/{section}' in layout, f'Nav missing {section}'
    assert f'"{section}"' in input_page, f'Route case missing {section}'
assert '/products' in layout
assert 'Danh mục đầu vào</span></NavLink>' not in layout
assert '<option value="50">50</option>' in input_page and '<option value="50">50</option>' in products
assert 'PageRows' in input_page and 'PageRows' in products
assert 'Skip((CurrentPage-1)*pageSize)' in input_page and 'Take(Math.Min(50,pageSize))' in products
assert 'selected.Count>0' in input_page and 'selected.Count>0' in products
assert 'showEditor && (editingId.HasValue?canUpdate:canCreate)' in input_page
assert 'if(showEditor && (canCreate||canUpdate))' in products
assert 'Gate.RequireAsync' in input_page and 'Gate.RequireAsync' in products
assert 'UpdateSupplierGroupPrice' in backend and 'UpdateCustomerGroupPrice' in backend
assert 'DeleteEntity' in backend and 'DeleteCampaign' in backend
assert "'\\uFEFF'" in script
assert 'js/catalog-export.js' in app
print(f'PASS: {len(sections)+1} Nav pages, 50 rows/page, delayed CRUD, permission guard, selection/export CSV UTF-8')

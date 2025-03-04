import { createLink } from 'application/constants/create-link';

export const Links = {
  Unauthorized: {
    Login: createLink('/login'),
  },
  Authorized: {
    Dashboard: createLink('/'),
    Products: createLink('/products'),
    ProductDetails: createLink('/products/:id'),
    CreateProduct: createLink('/products/create'),
    UiKit: createLink('/uikit'),
  },
};
